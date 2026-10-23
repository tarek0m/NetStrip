using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetStrip.Core;
using NetStrip.Core.Presentation;
using NetStrip.Theme;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NetStrip.Strip;

public partial class StripWindow : Window, IStripSurface
{
    private const double BaseFontSizeDip = 12;          // 9 pt at 96 dpi
    private const double HorizontalPaddingDip = 8;
    private const double CapsulePaddingXDip = 14;
    private const double CapsulePaddingYDip = 6;

    private readonly ThemeColors _theme;
    private readonly IdleTracker _idle = new();
    private readonly StripViewModel _vm = new();

    private StripMode _mode = StripMode.Full;
    private bool _hidden;
    private bool _faded;
    private bool _stylesApplied;
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
    }

    public event Action? FlyoutRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PauseToggled;

    /// <summary>Raised when a floating-mode drag ends, with the new top-left in screen pixels.</summary>
    public event Action<PixelPoint>? FloatingMoved;

    // ---- IStripSurface ------------------------------------------------------------------------

    public nint Handle
    {
        get
        {
            var h = new WindowInteropHelper(this).EnsureHandle();
            EnsureExtendedStyles(h);
            return h;
        }
    }

    public (int Width, int Height) Measure(StripMode mode, double dpiScale, int taskbarThickness)
    {
        var lineHeight = MeasureText("↓", out _).Height;
        switch (mode)
        {
            case StripMode.Compact:
                MeasureText("↓999 ↑999 MB/s", out var compactWidth);
                return (Px(compactWidth + HorizontalPaddingDip, dpiScale), taskbarThickness);
            case StripMode.Floating:
                MeasureText("↓ 999.9 MB/s", out var capsuleWidth);
                return (Px(capsuleWidth + HorizontalPaddingDip + 2 * CapsulePaddingXDip, dpiScale),
                        Px(2 * lineHeight + 2 * CapsulePaddingYDip, dpiScale));
            default:
                MeasureText("↓ 999.9 MB/s", out var fullWidth);
                return (Px(fullWidth + HorizontalPaddingDip, dpiScale), taskbarThickness);
        }
    }

    public void Apply(StripPlacement placement)
    {
        var hwnd = new HWND(Handle);
        SetMode(placement.Mode);

        var b = placement.Bounds;
        if (placement.Mode == StripMode.Floating)
        {
            Topmost = true;
            PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
        else
        {
            Topmost = false;
            PInvoke.SetWindowPos(hwnd, HWND.Null, b.X, b.Y, b.Width, b.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }

        if (!_hidden && !IsVisible) base.Show();
    }

    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden)
        {
            if (IsVisible) Hide();
        }
        else if (!IsVisible && _stylesApplied)
        {
            base.Show();
        }
    }

    // ---- Data ---------------------------------------------------------------------------------

    public void Show(Sample s, Totals today, SpeedUnits units, bool idleFadeEnabled)
    {
        var changes = _vm.Update(s, today, units);

        if ((changes & StripViewModel.Changes.Up) != 0) UpValue.Text = _vm.UpText;
        if ((changes & StripViewModel.Changes.Down) != 0) DownValue.Text = _vm.DownText;
        if ((changes & StripViewModel.Changes.Compact) != 0)
        {
            CompactDownRun.Text = _vm.CompactDown;
            CompactUpRun.Text = _vm.CompactUp;
        }
        if ((changes & StripViewModel.Changes.Tooltip) != 0) Root.ToolTip = _vm.Tooltip;
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
            Root.Background = Brushes.Transparent;   // keep the whole strip hit-testable
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

    private void OnClosed(object? sender, EventArgs e) => _theme.Changed -= OnThemeChanged;

    /// <summary>Size of <paramref name="text"/> in the strip font, in device-independent pixels.</summary>
    private Size MeasureText(string text, out double width)
    {
        // A TextBlock honours Typography.NumeralAlignment, which FormattedText cannot express.
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = FontFamily,
            FontSize = BaseFontSizeDip,
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

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnPauseClick(object sender, RoutedEventArgs e) => PauseToggled?.Invoke(PauseItem.IsChecked);
    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}
