using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NetStrip.Core;
using NetStrip.Core.Presentation;
using NetStrip.Platform;
using NetStrip.Theme;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NetStrip.Strip;

public partial class StripWindow : Window, IStripSurface, IDisposable
{
    private const double BaseFontSizeDip = 12;          // 9 pt at 96 dpi
    internal const double CompactFontSizeDip = 10;      // 7.5 pt at 96 dpi
    private const double HorizontalPaddingDip = 8;
    private const double CompactPaddingDip = 4;
    private const double CapsulePaddingXDip = 14;
    private const double CapsulePaddingYDip = 6;
    private const double ArrowGapDip = 6;               // the arrows' right margin in StripWindow.xaml
    private const double RoundingSlackDip = 1;          // layout rounding can take a fraction of a pixel per element
    private const string WidestSpeed = "999.9 MB/s";

    // A layered window passes clicks on fully transparent pixels through to the window behind it (the taskbar), so
    // "Transparent" would leave only the glyphs clickable. Alpha 1/255 is invisible but catches clicks everywhere.
    private static readonly Brush HitTestableClear = Frozen(Color.FromArgb(0x01, 0, 0, 0));

    private readonly ThemeColors _theme;
    private readonly IdleTracker _idle = new();
    private readonly StripViewModel _vm = new();

    private StripMode _mode = StripMode.Full;
    private bool _hidden;
    private bool _placed;
    private bool _layoutLogPending;
    private (int Width, int Height) _sizePx;
    private double _fittedDpiScale;
    private bool _faded;
    private bool _stylesApplied;
    private bool _closed;
    private bool _disposed;
    private readonly nint _handle;
    private Point _pressPoint;
    private bool _pressed;
    private bool _dragged;

    public StripWindow(ThemeColors theme)
    {
        _theme = theme;
        InitializeComponent();
        AutomationProperties.SetName(Root, "Network speed");
        AutomationProperties.SetName(this, "Network speed");
        ApplyTheme();
        _theme.Changed += OnThemeChanged;
        SetMode(StripMode.Full);

        // TaskbarHost calls SetParent right after construction, so the HWND (with its styles) must exist now.
        _handle = new WindowInteropHelper(this).EnsureHandle();
        EnsureExtendedStyles(_handle);
    }

    public event Action? FlyoutRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PauseToggled;

    /// <summary>Raised when a floating-mode drag ends, with the new top-left in screen pixels.</summary>
    public event Action<PixelPoint>? FloatingMoved;

    /// <summary>
    /// The user's saved floating top-left (screen pixels). When set, Floating placements keep their size but use this
    /// position, clamped into the work area of the nearest monitor, instead of the host's computed one (spec §6).
    /// </summary>
    public PixelPoint? FloatingPosition { get; set; }

    /// <summary>Keeps the "Pause updates" menu check in sync when pausing happens elsewhere (another strip, a new window).</summary>
    public void SetPaused(bool paused) => PauseItem.IsChecked = paused;

    // ---- IStripSurface ------------------------------------------------------------------------

    public nint Handle => _handle;

    public (int Width, int Height) Measure(StripMode mode, double dpiScale, int taskbarThickness)
    {
        var lineHeight = MeasureText("↓", BaseFontSizeDip, out _).Height;
        switch (mode)
        {
            case StripMode.Compact:
                MeasureText("↓999 ↑999 MB/s", CompactFontSizeDip, out var compactWidth);
                return (Px(compactWidth + CompactPaddingDip, dpiScale), taskbarThickness);
            case StripMode.Floating:
                return (Px(FullContentWidth() + HorizontalPaddingDip + 2 * CapsulePaddingXDip, dpiScale),
                        Px(2 * lineHeight + 2 * CapsulePaddingYDip, dpiScale));
            default:
                return (Px(FullContentWidth() + HorizontalPaddingDip, dpiScale), taskbarThickness);
        }
    }

    /// <summary>Mirrors FullLayout: arrow column (glyph + gap), then the widest speed text.</summary>
    private double FullContentWidth()
    {
        MeasureText("↓", BaseFontSizeDip, out var arrow);
        MeasureText(WidestSpeed, BaseFontSizeDip, out var speed);
        return arrow + ArrowGapDip + speed + RoundingSlackDip;
    }

    public void Apply(StripPlacement placement)
    {
        // Synchronous, physical pixels. Full/Compact bounds are relative to the parent's client area (the window is a
        // child by now); Floating bounds are screen coordinates (TaskbarHost has already made it top-level).
        var hwnd = new HWND(_handle);
        var floating = placement.Mode == StripMode.Floating;
        SetMode(placement.Mode);

        var b = placement.Bounds;
        if (floating)
        {
            if (FloatingPosition is { } saved)
            {
                var work = Monitors.WorkAreaNear(new PixelRect(saved.X, saved.Y, b.Width, b.Height));
                b = b with
                {
                    X = Math.Max(work.X, Math.Min(saved.X, work.X + work.Width - b.Width)),
                    Y = Math.Max(work.Y, Math.Min(saved.Y, work.Y + work.Height - b.Height)),
                };
            }
            PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
        else
        {
            PInvoke.SetWindowPos(hwnd, HWND.Null, b.X, b.Y, b.Width, b.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }

        // The width above comes from the taskbar's DPI; WPF lays the text out at the window's own DPI. If those
        // differ, text is clipped, so record both whenever the placement changes.
        App.Log.Info($"Strip placed: {placement.Mode} {b.Width}x{b.Height} px at ({b.X}, {b.Y}); " +
                     $"taskbar DPI scale {placement.DpiScale:0.###}, WPF DPI scale {VisualTreeHelper.GetDpi(this).DpiScaleX:0.###}");
        _layoutLogPending = true;

        _sizePx = (b.Width, b.Height);
        FitContentToWindow();

        _placed = true;
        if (!_hidden && !IsVisible) base.Show();
    }

    /// <summary>Current outer bounds in screen pixels.</summary>
    public PixelRect ScreenBounds
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var origin = PointToScreen(new Point(0, 0));
            return new PixelRect((int)Math.Round(origin.X), (int)Math.Round(origin.Y),
                (int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        }
    }

    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden)
        {
            if (IsVisible) Hide();
        }
        else if (!IsVisible && _stylesApplied && _placed)   // never show a window that has not been positioned yet
        {
            base.Show();
        }
    }

    // ---- Data ---------------------------------------------------------------------------------

    public void Show(Sample s, Totals today, SpeedUnits units, bool idleFadeEnabled)
    {
        // Moving to a taskbar on another monitor can change the window's DPI after placement.
        if (VisualTreeHelper.GetDpi(this).DpiScaleX != _fittedDpiScale) FitContentToWindow();

        var changes = _vm.Update(s, today, units);

        if ((changes & StripViewModel.Changes.Up) != 0) UpValue.Text = _vm.UpText;
        if ((changes & StripViewModel.Changes.Down) != 0) DownValue.Text = _vm.DownText;
        if ((changes & StripViewModel.Changes.Compact) != 0)
        {
            CompactDownRun.Text = _vm.CompactDown;
            CompactUpRun.Text = _vm.CompactUp;
        }
        if ((changes & StripViewModel.Changes.Tooltip) != 0) Root.ToolTip = _vm.Tooltip;
        if (_layoutLogPending && _placed)
        {
            _layoutLogPending = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, LogLayout);   // after this text is laid out
        }
        if ((changes & StripViewModel.Changes.Name) != 0)
        {
            AutomationProperties.SetName(Root, _vm.AccessibleName);
            AutomationProperties.SetName(this, _vm.AccessibleName);
        }

        var idle = _idle.Update(s);
        var fade = idleFadeEnabled && idle;
        if (fade != _faded)
        {
            _faded = fade;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = fade ? 0.45 : 1.0,
                Duration = TimeSpan.FromMilliseconds(fade ? 200 : 120),
                FillBehavior = FillBehavior.HoldEnd,
            });
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitContentToWindow();
    }

    /// <summary>
    /// WPF's Window keeps laying out at least the system minimum window width (166 px at 125 %) even after the HWND is
    /// made narrower, so right-aligned text landed outside the visible area. Size the content to the real HWND, in
    /// this window's DIPs, so everything inside it is visible at any resolution and scale.
    /// </summary>
    private void FitContentToWindow()
    {
        if (_sizePx.Width <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _fittedDpiScale = dpi.DpiScaleX;
        Root.Width = _sizePx.Width / dpi.DpiScaleX;
        Root.Height = _sizePx.Height / dpi.DpiScaleY;
    }

    // ---- Mode & theme -------------------------------------------------------------------------

    private void SetMode(StripMode mode)
    {
        _mode = mode;
        var compact = mode == StripMode.Compact;
        FullLayout.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactLayout.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        var floating = mode == StripMode.Floating;
        Inner.Margin = floating
            ? new Thickness(CapsulePaddingXDip, CapsulePaddingYDip, CapsulePaddingXDip, CapsulePaddingYDip)
            : compact ? new Thickness(CompactPaddingDip / 2, 0, CompactPaddingDip / 2, 0)
                      : new Thickness(HorizontalPaddingDip / 2, 0, HorizontalPaddingDip / 2, 0);
        Root.BorderThickness = new Thickness(floating ? 1 : 0);
        Root.CornerRadius = new CornerRadius(floating ? 100 : 0);   // clamped to half the height by the border
        ApplyCapsuleBrushes();
    }

    private void OnThemeChanged() => ApplyTheme();

    private void ApplyTheme()
    {
        var number = _theme.TaskbarForeground;
        UpValue.Foreground = number;
        DownValue.Foreground = number;
        CompactDownRun.Foreground = number;
        CompactUpRun.Foreground = number;
        UpArrow.Foreground = _theme.UploadBrush;
        CompactUpArrow.Foreground = _theme.UploadBrush;
        DownArrow.Foreground = _theme.DownloadBrush;
        CompactDownArrow.Foreground = _theme.DownloadBrush;
        ApplyCapsuleBrushes();
    }

    private void ApplyCapsuleBrushes()
    {
        if (_mode == StripMode.Floating)
        {
            // Solid, Mica-like surface so the capsule reads on any wallpaper.
            var light = _theme.SystemUsesLightTheme;
            Root.Background = Frozen(light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x2C, 0x2C, 0x2C));
            Root.BorderBrush = Frozen(light ? Color.FromArgb(0x26, 0, 0, 0) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            Root.Background = HitTestableClear;
            Root.BorderBrush = Brushes.Transparent;
        }
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---- Window plumbing ----------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e) =>
        EnsureExtendedStyles(new WindowInteropHelper(this).Handle);

    private void EnsureExtendedStyles(nint handle)
    {
        if (_stylesApplied || handle == 0) return;
        _stylesApplied = true;
        var hwnd = new HWND(handle);
        var ex = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var add = (nint)(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE);
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex | add);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _theme.Changed -= OnThemeChanged;
    }

    /// <summary>Closes the window and detaches every handler. Safe after Explorer has already destroyed the window.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _theme.Changed -= OnThemeChanged;
        FlyoutRequested = null;
        SettingsRequested = null;
        ExitRequested = null;
        PauseToggled = null;
        FloatingMoved = null;
        if (_closed) return;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing because its parent (the taskbar) went away.
        }
    }

    /// <summary>Size of <paramref name="text"/> in the strip font, in device-independent pixels.</summary>
    private Size MeasureText(string text, double fontSize, out double width)
    {
        // A TextBlock honours Typography.NumeralAlignment, which FormattedText cannot express.
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = FontFamily,
            FontSize = fontSize,
            TextWrapping = TextWrapping.NoWrap,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        width = tb.DesiredSize.Width;
        return tb.DesiredSize;
    }

    /// <summary>
    /// Diagnostics for the clipped speed text: what WPF laid out versus what the text needs, and which sibling windows
    /// above the strip overlap it. Runs after the first sample following each placement change.
    /// </summary>
    private unsafe void LogLayout()
    {
        if (_closed) return;
        try
        {
            var hwnd = new HWND(_handle);
            if (!PInvoke.GetWindowRect(hwnd, out var own)) return;
            MeasureText(UpValue.Text, UpValue.FontSize, out var upNeeds);
            MeasureText(DownValue.Text, DownValue.FontSize, out var downNeeds);
            var line = new System.Text.StringBuilder()
                .Append($"Strip layout: window {own.right - own.left}x{own.bottom - own.top} px at screen ({own.left}, {own.top}), ")
                .Append($"WPF {ActualWidth:0.#}x{ActualHeight:0.#} DIP, content {Root.ActualWidth:0.#}x{Root.ActualHeight:0.#} DIP; ")
                .Append($"up '{UpValue.Text}' needs {upNeeds:0.#} DIP, has {UpValue.ActualWidth:0.#}; ")
                .Append($"down '{DownValue.Text}' needs {downNeeds:0.#} DIP, has {DownValue.ActualWidth:0.#}; ")
                .Append($"font '{UpValue.FontFamily}' {UpValue.FontSize:0.#}");

            const int MaxClassName = 128;
            char* className = stackalloc char[MaxClassName];
            var above = 0;
            for (var w = PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_HWNDPREV); !w.IsNull; w = PInvoke.GetWindow(w, GET_WINDOW_CMD.GW_HWNDPREV))
            {
                if (!PInvoke.IsWindowVisible(w) || !PInvoke.GetWindowRect(w, out var r)) continue;
                if (r.left >= own.right || own.left >= r.right || r.top >= own.bottom || own.top >= r.bottom) continue;
                var length = PInvoke.GetClassName(w, className, MaxClassName);
                line.Append($"; covered by '{new string(className, 0, length)}' at ({r.left}, {r.top}) {r.right - r.left}x{r.bottom - r.top}");
                above++;
            }
            if (above == 0) line.Append("; no overlapping window above it");
            App.Log.Info(line.ToString());
        }
        catch (Exception ex)
        {
            App.Log.Warn("Logging the strip layout failed", ex);
        }
    }

    private static int Px(double dip, double dpiScale) => (int)Math.Ceiling(dip * dpiScale);

    // ---- Mouse ---------------------------------------------------------------------------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _dragged = false;
        _pressPoint = e.GetPosition(this);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || _mode != StripMode.Floating || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragged = true;
        _pressed = false;
        try
        {
            DragMove();   // blocks until the button is released
        }
        catch (InvalidOperationException)
        {
            return;       // button was released before the drag started
        }
        var origin = PointToScreen(new Point(0, 0));
        FloatingMoved?.Invoke(new PixelPoint((int)Math.Round(origin.X), (int)Math.Round(origin.Y)));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasClick = _pressed && !_dragged;
        _pressed = false;
        _dragged = false;
        if (wasClick) FlyoutRequested?.Invoke();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnPauseClick(object sender, RoutedEventArgs e) => PauseToggled?.Invoke(PauseItem.IsChecked);
    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}
