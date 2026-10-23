using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using NetStrip.Core;
using NetStrip.Core.Formatting;
using NetStrip.Core.Presentation;
using NetStrip.Platform;
using NetStrip.Theme;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NetStrip.Flyout;

public partial class FlyoutWindow : Window
{
    private const int GapDip = 8;
    private const int MarginDip = 8;

    private readonly ISampleSource _source;
    private readonly GraphBuffer _graph;
    private readonly IUsageQuery _usage;
    private readonly ISettingsStore _settings;
    private readonly ThemeColors _theme;
    private readonly ObservableCollection<string> _notices = [];

    private Sample? _last;
    private volatile bool _visibleFlag;
    private bool _micaTried;
    private int _noticesShown;

    public FlyoutWindow(ISampleSource source, GraphBuffer graph, IUsageQuery usage, ISettingsStore settings, ThemeColors theme)
    {
        _source = source;
        _graph = graph;
        _usage = usage;
        _settings = settings;
        _theme = theme;
        InitializeComponent();

        NoticeList.ItemsSource = _notices;
        Graph.Bind(graph, settings.Current.Units);
        ApplyGraphBrushes();

        _source.SampleProduced += OnSampleProduced;
        _settings.Changed += OnSettingsChanged;
        _theme.Changed += OnThemeChanged;
    }

    /// <summary>Raised when the gear button is pressed.</summary>
    public event Action? SettingsRequested;

    public bool IsOpen => IsVisible;

    /// <summary>
    /// Shows the flyout next to the strip (screen pixels), on the taskbar side given by <paramref name="edge"/>,
    /// clamped into the monitor work area.
    /// </summary>
    public void ShowAt(PixelRect stripScreenRect, TaskbarEdge edge)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (!_micaTried)
        {
            _micaTried = true;
            var mica = Mica.Apply(this);
            App.Log.Info($"Flyout backdrop: {(mica ? "Mica" : "solid themed background")}");
        }

        RefreshAll();

        var dpi = Monitors.DpiScaleNear(stripScreenRect);
        var work = Monitors.WorkAreaNear(stripScreenRect);
        var widthPx = (int)Math.Ceiling(Width * dpi);

        // Estimate the height from the content before showing, then correct once real layout exists.
        if (Content is UIElement root)
        {
            root.Measure(new Size(Width, double.PositiveInfinity));
            Place(handle, stripScreenRect, edge, widthPx, (int)Math.Ceiling(root.DesiredSize.Height * dpi), work, dpi);
        }

        Show();
        Activate();
        UpdateLayout();

        var actual = VisualTreeHelper.GetDpi(this);
        Place(handle, stripScreenRect, edge,
            (int)Math.Ceiling(ActualWidth * actual.DpiScaleX), (int)Math.Ceiling(ActualHeight * actual.DpiScaleY), work, dpi);
    }

    private static void Place(nint handle, PixelRect strip, TaskbarEdge edge, int w, int h, PixelRect work, double dpi)
    {
        var p = FlyoutPlacement.Compute(strip, edge, w, h, work, (int)Math.Round(GapDip * dpi), (int)Math.Round(MarginDip * dpi));
        PInvoke.SetWindowPos(new HWND(handle), HWND.HWND_TOPMOST, p.X, p.Y, w, h, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public void SetUpdate(UpdateInfo? update)
    {
        if (update is null)
        {
            UpdatePanel.Visibility = Visibility.Collapsed;
            return;
        }
        var v = update.Version;
        UpdateRun.Text = $"Update available — v{v.ToString(v.Build > 0 ? 3 : 2)}";
        UpdateLink.NavigateUri = update.ReleaseUrl;
        AutomationProperties.SetName(UpdateLink, UpdateRun.Text);
        UpdatePanel.Visibility = Visibility.Visible;
    }

    /// <summary>Adds a one-time notice; it is shown until the flyout has been seen once and closed.</summary>
    public void AddNotice(string notice)
    {
        if (string.IsNullOrWhiteSpace(notice) || _notices.Contains(notice)) return;
        _notices.Add(notice);
    }

    // ---- Live data ----------------------------------------------------------------------------

    private void OnSampleProduced(Sample s)
    {
        // Sampler thread: cheap early-out while hidden, otherwise marshal after the app host's own handler.
        if (!_visibleFlag) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _last = s;
            if (IsVisible) RefreshAll();
        });
    }

    private void OnSettingsChanged(AppSettings _)
    {
        Graph.Units = _settings.Current.Units;
        if (IsVisible) RefreshAll();
    }

    private void OnThemeChanged() => ApplyGraphBrushes();

    private void ApplyGraphBrushes()
    {
        if (SystemParameters.HighContrast)
        {
            Graph.DownloadBrush = SystemColors.HighlightBrush;
            Graph.UploadBrush = SystemColors.WindowTextBrush;
        }
        else
        {
            Graph.DownloadBrush = _theme.DownloadBrush;
            Graph.UploadBrush = _theme.UploadBrush;
        }
        LiveUpArrow.Foreground = Graph.UploadBrush;
        LiveDownArrow.Foreground = Graph.DownloadBrush;
    }

    private void RefreshAll()
    {
        var units = _settings.Current.Units;
        Graph.Units = units;

        UpdateHeader();

        if ((_last ?? (_graph.Items.Count > 0 ? _graph.Items[^1] : null)) is { } s)
        {
            SetText(LiveUp, SpeedFormatter.FormatSpeed(s.UpBytesPerSec, units));
            SetText(LiveDown, SpeedFormatter.FormatSpeed(s.DownBytesPerSec, units));
        }
        else
        {
            SetText(LiveUp, SpeedFormatter.FormatSpeed(0, units));
            SetText(LiveDown, SpeedFormatter.FormatSpeed(0, units));
        }
        AutomationProperties.SetName(LiveUp, $"Upload {LiveUp.Text}");
        AutomationProperties.SetName(LiveDown, $"Download {LiveDown.Text}");

        var today = _usage.Today(AdapterIds.All);
        var period = _usage.ThisPeriod(AdapterIds.All);
        SetText(TodayDown, "↓ " + SpeedFormatter.FormatTotal(today.DownBytes));
        SetText(TodayUp, "↑ " + SpeedFormatter.FormatTotal(today.UpBytes));
        SetText(PeriodDown, "↓ " + SpeedFormatter.FormatTotal(period.DownBytes));
        SetText(PeriodUp, "↑ " + SpeedFormatter.FormatTotal(period.UpBytes));
        AutomationProperties.SetName(TodayDown, $"Today download {SpeedFormatter.FormatTotal(today.DownBytes)}");
        AutomationProperties.SetName(TodayUp, $"Today upload {SpeedFormatter.FormatTotal(today.UpBytes)}");
        AutomationProperties.SetName(PeriodDown, $"This period download {SpeedFormatter.FormatTotal(period.DownBytes)}");
        AutomationProperties.SetName(PeriodUp, $"This period upload {SpeedFormatter.FormatTotal(period.UpBytes)}");

        UpdateCap();
        Graph.Refresh();
    }

    private void UpdateHeader()
    {
        var adapter = _source.CurrentAdapter;
        string glyph, name, ip;
        if (adapter is null)
        {
            glyph = "";
            name = "Disconnected";
            ip = "";
        }
        else
        {
            glyph = adapter.Kind switch
            {
                AdapterKind.Ethernet => "",
                AdapterKind.WiFi => "",
                AdapterKind.Cellular => "",
                _ => "",
            };
            name = adapter.Name;
            ip = adapter.IPv4 ?? "";
        }
        SetText(HeaderGlyph, glyph);
        AutomationProperties.SetName(HeaderGlyph, adapter?.Kind.ToString() ?? "Disconnected");
        SetText(HeaderName, name);
        SetText(HeaderIp, ip);
        HeaderIp.Visibility = ip.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(HeaderPanel,
            adapter is null ? "Disconnected" : $"{adapter.Kind} connection, {name}{(ip.Length > 0 ? ", " + ip : "")}");
    }

    private void UpdateCap()
    {
        if (_usage.CapProgress() is not { Cap: > 0 } cap)
        {
            CapPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var fraction = (double)cap.Used / cap.Cap;
        CapPanel.Visibility = Visibility.Visible;
        CapBar.Value = Math.Clamp(fraction, 0, 1);
        SetText(CapText, $"{SpeedFormatter.FormatTotal(cap.Used)} of {SpeedFormatter.FormatTotal(cap.Cap)}");

        if (fraction >= 1.0) CapBar.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
        else if (fraction >= 0.8) CapBar.SetResourceReference(ForegroundProperty, "SystemFillColorCautionBrush");
        else CapBar.ClearValue(ForegroundProperty);

        AutomationProperties.SetName(CapBar, $"Monthly data cap used, {fraction:P0}, {CapText.Text}");
    }

    private static void SetText(System.Windows.Controls.TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal)) block.Text = text;
    }

    // ---- Window behaviour ---------------------------------------------------------------------

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsVisible) Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Hide();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _visibleFlag = IsVisible;
        if (IsVisible)
        {
            _noticesShown = _notices.Count;
            return;
        }
        // Notices are one-time: drop the ones the user has now had a chance to read.
        for (var i = 0; i < _noticesShown && _notices.Count > 0; i++) _notices.RemoveAt(0);
        _noticesShown = 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _source.SampleProduced -= OnSampleProduced;
        _settings.Changed -= OnSettingsChanged;
        _theme.Changed -= OnThemeChanged;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke();
    }

    private void OnUpdateNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log.Warn("Could not open release page", ex);
        }
        Hide();
    }
}
