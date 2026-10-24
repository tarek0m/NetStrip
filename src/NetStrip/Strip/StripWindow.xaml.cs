using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    // Floating strip: a rounded rectangle with Windows 11's window corner radius, so it looks like part of the shell.
    private const double FloatingPaddingXDip = 10;
    private const double FloatingPaddingYDip = 6;
    private const double FloatingCornerRadiusDip = 8;
    private const double ArrowGapDip = 6;               // the arrows' right margin in StripWindow.xaml
    private const double RoundingSlackDip = 1;          // layout rounding can take a fraction of a pixel per element
    private const string WidestSpeed = "999.9 MB/s";
    private const double VerticalPaddingDip = 2;
    private const double VerticalArrowGapDip = 3;       // the vertical arrows' right margin in StripWindow.xaml
    private const double VerticalUnitGapDip = 3;        // the upload unit's bottom margin in StripWindow.xaml
    private const string WidestNumber = "999.9";
    private const string WidestCompactSpeed = "999.9 MB";
    private const double CompactArrowGapDip = 3;        // the compact arrows' right margin in StripWindow.xaml
    private const string WidestUnit = "MB/s";
    // WPF scales the default control font by 7/6 under the Fluent theme (ThemeManager.DefaultFluentFontSizeFactor).
    private const double FluentFontSizeFactor = 7.0 / 6.0;

    // A layered window passes clicks on fully transparent pixels through to the window behind it (the taskbar), so
    // "Transparent" would leave only the glyphs clickable. Alpha 1/255 is invisible but catches clicks everywhere.
    private static readonly Brush HitTestableClear = Frozen(Color.FromArgb(0x01, 0, 0, 0));

    private readonly ThemeColors _theme;
    private readonly IdleTracker _idle = new();
    private readonly StripViewModel _vm = new();

    private StripMode _mode = StripMode.Full;
    private bool _hidden;          // fullscreen hiding (SetHidden)
    private bool _crowdHidden;     // the taskbar is full and the user chose to hide (StripPlacement.Hidden)
    private bool _placed;
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

    /// <summary>
    /// Raised, synchronously, right before the context menu or tooltip opens, so the host can load the Fluent theme
    /// they are styled with. The argument names what is opening, for the log.
    /// </summary>
    public event Action<string>? ThemeNeeded;

    /// <summary>Raised when a floating-mode drag ends, with the new top-left in screen pixels.</summary>
    public event Action<PixelPoint>? FloatingMoved;

    /// <summary>
    /// The user's saved floating top-left (screen pixels). When set, Floating placements keep their size but use this
    /// position, clamped into the work area of the nearest monitor, instead of the host's computed one (spec §6).
    /// </summary>
    public PixelPoint? FloatingPosition { get; set; }

    /// <summary>Keeps the "Pause speed display" menu check in sync when pausing happens elsewhere (another strip, a new window).</summary>
    public void SetPaused(bool paused) => PauseItem.IsChecked = paused;

    // ---- IStripSurface ------------------------------------------------------------------------

    public nint Handle => _handle;

    public (int Width, int Height) Measure(StripMode mode, double dpiScale, int taskbarThickness)
    {
        var lineHeight = MeasureText("↓", BaseFontSizeDip, out _).Height;
        switch (mode)
        {
            case StripMode.Compact:
                return (Px(CompactContentWidth() + CompactPaddingDip, dpiScale), taskbarThickness);
            case StripMode.Floating:
                return (Px(FullContentWidth() + HorizontalPaddingDip + 2 * FloatingPaddingXDip, dpiScale),
                        Px(2 * lineHeight + 2 * FloatingPaddingYDip, dpiScale));
            case StripMode.Vertical:
                // Natural size; StripLayout scales it down uniformly if the side taskbar is narrower.
                var unitHeight = MeasureText(WidestUnit, CompactFontSizeDip, out _).Height;
                return (Px(VerticalContentWidth() + 2 * VerticalPaddingDip, dpiScale),
                        Px(2 * (lineHeight + unitHeight) + VerticalUnitGapDip + 2 * VerticalPaddingDip, dpiScale));
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

    /// <summary>Mirrors CompactGrid: arrow column (glyph + gap), then the widest speed without "/s", all in the compact font.</summary>
    private double CompactContentWidth()
    {
        MeasureText("↓", CompactFontSizeDip, out var arrow);
        MeasureText(WidestCompactSpeed, CompactFontSizeDip, out var speed);
        return arrow + CompactArrowGapDip + speed + RoundingSlackDip;
    }

    /// <summary>Mirrors VerticalGrid: arrow column (glyph + gap), then the wider of the widest number and unit.</summary>
    private double VerticalContentWidth()
    {
        MeasureText("↓", BaseFontSizeDip, out var arrow);
        MeasureText(WidestNumber, BaseFontSizeDip, out var number);
        MeasureText(WidestUnit, CompactFontSizeDip, out var unit);
        return arrow + VerticalArrowGapDip + Math.Max(number, unit) + RoundingSlackDip;
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

        App.Log.Info($"Strip placed: {placement.Mode} {b.Width}x{b.Height} px at ({b.X}, {b.Y}), DPI scale {placement.DpiScale:0.###}");

        _sizePx = (b.Width, b.Height);
        FitContentToWindow();

        _placed = true;
        _crowdHidden = placement.Hidden;
        if (_crowdHidden)
        {
            if (IsVisible) Hide();
        }
        else if (!_hidden && !IsVisible)
        {
            base.Show();
        }
    }

    /// <summary>
    /// Current outer bounds in screen pixels, from the HWND itself: WPF's ActualWidth stays at the system minimum
    /// window width (see <see cref="FitContentToWindow"/>), which put the flyout beside a strip that wasn't there.
    /// </summary>
    public PixelRect ScreenBounds =>
        PInvoke.GetWindowRect(new HWND(_handle), out var r)
            ? new PixelRect(r.left, r.top, r.right - r.left, r.bottom - r.top)
            : default;

    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden)
        {
            if (IsVisible) Hide();
        }
        else if (!IsVisible && !_crowdHidden && _stylesApplied && _placed)   // never show an unpositioned or crowded-out window
        {
            base.Show();
        }
    }

    // ---- Data ---------------------------------------------------------------------------------

    /// <param name="cap">This cycle's data-cap progress in bytes; null when no cap is set.</param>
    public void Show(Sample s, Totals today, (long Used, long Cap)? cap, SpeedUnits units, bool idleFadeEnabled)
    {
        // Moving to a taskbar on another monitor can change the window's DPI after placement.
        if (VisualTreeHelper.GetDpi(this).DpiScaleX != _fittedDpiScale) FitContentToWindow();

        var changes = _vm.Update(s, today, cap, units);

        if ((changes & StripViewModel.Changes.Up) != 0)
        {
            UpValue.Text = _vm.UpText;
            (VerticalUpNumber.Text, VerticalUpUnit.Text) = StripViewModel.Split(_vm.UpText);
        }
        if ((changes & StripViewModel.Changes.Down) != 0)
        {
            DownValue.Text = _vm.DownText;
            (VerticalDownNumber.Text, VerticalDownUnit.Text) = StripViewModel.Split(_vm.DownText);
        }
        if ((changes & StripViewModel.Changes.Compact) != 0)
        {
            CompactDownValue.Text = _vm.CompactDown;
            CompactUpValue.Text = _vm.CompactUp;
        }
        if ((changes & StripViewModel.Changes.Tooltip) != 0) Root.ToolTip = _vm.Tooltip;
        if ((changes & StripViewModel.Changes.Cap) != 0) ApplyArrowBrushes();
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
            // Fade the text, not Root: Root's alpha-1 background would round to fully transparent at 0.45 opacity,
            // and a layered window passes clicks on fully transparent pixels through to the taskbar.
            Inner.BeginAnimation(OpacityProperty, new DoubleAnimation
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
        var vertical = mode == StripMode.Vertical;
        FullLayout.Visibility = compact || vertical ? Visibility.Collapsed : Visibility.Visible;
        CompactLayout.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        VerticalLayout.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        if (vertical) VerticalGrid.Width = VerticalContentWidth();
        if (compact) CompactGrid.Width = CompactContentWidth();

        var floating = mode == StripMode.Floating;
        Inner.Margin = floating ? new Thickness(FloatingPaddingXDip, FloatingPaddingYDip, FloatingPaddingXDip, FloatingPaddingYDip)
            : compact ? new Thickness(CompactPaddingDip / 2, 0, CompactPaddingDip / 2, 0)
            : vertical ? new Thickness(VerticalPaddingDip)
            : new Thickness(HorizontalPaddingDip / 2, 0, HorizontalPaddingDip / 2, 0);
        Root.BorderThickness = new Thickness(floating ? 1 : 0);
        Root.CornerRadius = new CornerRadius(floating ? FloatingCornerRadiusDip : 0);
        ApplyFloatingBrushes();
    }

    private void OnThemeChanged() => ApplyTheme();

    private void ApplyTheme()
    {
        var number = _theme.TaskbarForeground;
        UpValue.Foreground = number;
        DownValue.Foreground = number;
        CompactDownValue.Foreground = number;
        CompactUpValue.Foreground = number;
        VerticalUpNumber.Foreground = number;
        VerticalUpUnit.Foreground = number;
        VerticalDownNumber.Foreground = number;
        VerticalDownUnit.Foreground = number;
        ApplyArrowBrushes();
        ApplyFloatingBrushes();
    }

    /// <summary>
    /// Upload/download colours normally; every arrow in every layout turns amber at 80 % of the data cap and red at
    /// 100 %, so the cap shows without opening the flyout (the tooltip carries the same state as text).
    /// </summary>
    private void ApplyArrowBrushes()
    {
        var capBrush = _vm.CapLevel switch
        {
            CapLevel.Reached100 => _theme.CapCriticalBrush,
            CapLevel.Warning80 => _theme.CapWarningBrush,
            _ => null,
        };
        var up = capBrush ?? _theme.UploadBrush;
        var down = capBrush ?? _theme.DownloadBrush;
        UpArrow.Foreground = up;
        CompactUpArrow.Foreground = up;
        VerticalUpArrow.Foreground = up;
        DownArrow.Foreground = down;
        CompactDownArrow.Foreground = down;
        VerticalDownArrow.Foreground = down;
    }

    private void ApplyFloatingBrushes()
    {
        if (_mode == StripMode.Floating)
        {
            // Solid, Mica-like surface so the floating strip reads on any wallpaper.
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
        ThemeNeeded = null;
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

    // Both events are raised before WPF builds the popup: the tooltip is created after ToolTipOpening returns, and the
    // menu is first parented into its popup (which re-reads its implicit styles) after ContextMenuOpening returns.
    // So the first menu and tooltip already render with the Fluent styles loaded here.
    private void OnToolTipOpening(object sender, ToolTipEventArgs e) => ThemeNeeded?.Invoke("strip tooltip");

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ThemeNeeded?.Invoke("strip menu");
        // MenuItem fixed its default font size when this window was built, before the theme existed, so it lacks the
        // Fluent scaling; the Fluent menu styles set no size. Give the menu the size it gets with the theme preloaded.
        if (Root.ContextMenu is { } menu) menu.FontSize = SystemFonts.MessageFontSize * FluentFontSizeFactor;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnPauseClick(object sender, RoutedEventArgs e) => PauseToggled?.Invoke(PauseItem.IsChecked);
    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}
