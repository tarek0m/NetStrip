using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
    private readonly AdapterViewSwitcher _views = new();

    private Sample? _last;
    private volatile bool _visibleFlag;
    private bool _micaApplied;
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
        UpdateBackdrop();

        // Browsing other adapters is a quick look: every open starts on what the strip shows.
        _views.Reset();
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
        UpdateRun.Text = $"New version available — v{v.ToString(v.Build > 0 ? 3 : 2)}";
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

    /// <summary>While true ("Pause speed display"), live speeds and the graph stay frozen; totals still refresh on open.</summary>
    public bool SpeedsPaused { get; set; }

    private void OnSampleProduced(Sample s)
    {
        // Sampler thread: cheap early-out while hidden or paused, otherwise marshal after the app host's own handler.
        if (!_visibleFlag || SpeedsPaused) return;
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

    private void OnThemeChanged()
    {
        ApplyGraphBrushes();
        if (_micaApplied) Mica.RefreshDarkMode(this);
    }

    /// <summary>
    /// Backdrop state follows the OS live: Mica is applied once (skipped in high contrast, removed if high contrast
    /// is turned on later), and the dark-mode tint is re-synced on every show so it never disagrees with the Fluent theme.
    /// </summary>
    private void UpdateBackdrop()
    {
        if (SystemParameters.HighContrast)
        {
            if (_micaApplied)
            {
                Mica.Remove(this);
                _micaApplied = false;
                App.Log.Info("Flyout backdrop: solid themed background (high contrast)");
            }
            return;
        }

        if (_micaApplied)
        {
            Mica.RefreshDarkMode(this);
            return;
        }

        if (!Mica.IsSupported) return;
        if (Mica.Apply(this))
        {
            _micaApplied = true;
            App.Log.Info("Flyout backdrop: Mica");
        }
    }

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

        UpdateViews();
        var view = _views.Current;
        if (!ReferenceEquals(Graph.View, view)) Graph.View = view;
        UpdateHeader(view);

        if ((_last ?? (_graph.Items.Count > 0 ? _graph.Items[^1] : null)) is { } s)
        {
            var (up, down) = view.RatesFor(s);
            SetText(LiveUp, SpeedFormatter.FormatSpeed(up, units));
            SetText(LiveDown, SpeedFormatter.FormatSpeed(down, units));
        }
        else
        {
            SetText(LiveUp, SpeedFormatter.FormatSpeed(0, units));
            SetText(LiveDown, SpeedFormatter.FormatSpeed(0, units));
        }
        AutomationProperties.SetName(LiveUp, $"Upload {LiveUp.Text}");
        AutomationProperties.SetName(LiveDown, $"Download {LiveDown.Text}");

        var today = _usage.Today(view.UsageScope);
        var period = _usage.ThisPeriod(view.UsageScope);
        SetText(TodayDown, "↓ " + SpeedFormatter.FormatTotal(today.DownBytes));
        SetText(TodayUp, "↑ " + SpeedFormatter.FormatTotal(today.UpBytes));
        SetText(PeriodDown, "↓ " + SpeedFormatter.FormatTotal(period.DownBytes));
        SetText(PeriodUp, "↑ " + SpeedFormatter.FormatTotal(period.UpBytes));
        AutomationProperties.SetName(TodayDown, $"Today download {SpeedFormatter.FormatTotal(today.DownBytes)}");
        AutomationProperties.SetName(TodayUp, $"Today upload {SpeedFormatter.FormatTotal(today.UpBytes)}");
        AutomationProperties.SetName(PeriodDown, $"This cycle download {SpeedFormatter.FormatTotal(period.DownBytes)}");
        AutomationProperties.SetName(PeriodUp, $"This cycle upload {SpeedFormatter.FormatTotal(period.UpBytes)}");

        UpdateCap();
        Graph.Refresh();
    }

    /// <summary>Rebuilds the adapter views from the current setting and adapters; the arrows show only with more than one.</summary>
    private void UpdateViews()
    {
        _views.Update(_settings.Current.Adapter, _source.CurrentAdapter?.Id, _source.Adapters);
        ViewSwitcher.Visibility = _views.CanSwitch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHeader(AdapterView view)
    {
        string glyph, name, detail, accessible;
        var adapter = view.Kind == AdapterViewKind.Adapter ? view.Adapter : _source.CurrentAdapter;
        if (view.Kind == AdapterViewKind.AllAdapters
            || (view.Kind == AdapterViewKind.Configured
                && string.Equals(_settings.Current.Adapter, AdapterIds.All, StringComparison.OrdinalIgnoreCase)))
        {
            // Speeds are the sum of every adapter, so there is no single "current" one to show.
            var connected = _source.Adapters.Count(IsConnected);
            glyph = connected > 0 ? "\uE968" : "\uE871";   // Network / Disconnected
            name = "All adapters";
            detail = connected > 0 ? $"{connected} connected" : "None connected";
            accessible = $"All adapters, {detail}";
        }
        else if (adapter is null)
        {
            glyph = "\uE871";
            name = "Disconnected";
            detail = "";
            accessible = name;
        }
        else if (!adapter.Connected)
        {
            // The chosen adapter is still there but its link is down (Wi-Fi dropped, cable out).
            glyph = "\uE871";
            name = adapter.Name;
            detail = "Disconnected";
            accessible = $"{adapter.Kind} connection, {name}, disconnected";
        }
        else
        {
            glyph = adapter.Kind switch
            {
                AdapterKind.Ethernet => "\uE839",
                AdapterKind.WiFi => "\uE701",
                AdapterKind.Cellular => "\uEC3B",
                _ => "\uE774",
            };
            name = adapter.Name;
            detail = adapter.IPv4 ?? "";
            accessible = $"{adapter.Kind} connection, {name}{(detail.Length > 0 ? ", " + detail : "")}";
        }
        SetText(HeaderGlyph, glyph);
        AutomationProperties.SetName(HeaderGlyph, name);
        SetText(HeaderName, name);
        SetText(HeaderIp, detail);
        HeaderIp.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_views.CanSwitch) accessible += $", {_views.Index + 1} of {_views.Views.Count}";
        AutomationProperties.SetName(HeaderPanel, accessible);
    }

    /// <summary>Shows the previous or next adapter view (flyout only: the strip and settings are untouched).</summary>
    private void SwitchView(bool next)
    {
        UpdateViews();
        if (!(next ? _views.Next() : _views.Previous())) return;
        RefreshAll();

        // The header is a polite live region: a screen reader announces the new view while focus stays on the arrow.
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(HeaderPanel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnPreviousViewClick(object sender, RoutedEventArgs e) => SwitchView(next: false);

    private void OnNextViewClick(object sender, RoutedEventArgs e) => SwitchView(next: true);

    /// <summary>Link up with a usable IPv4 address: not a self-assigned 169.254.x.x one (link-local, no network).</summary>
    private static bool IsConnected(AdapterInfo a) =>
        a.Connected && a.IPv4 is { Length: > 0 } ip && !ip.StartsWith("169.254.", StringComparison.Ordinal);

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
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Hide();
                break;
            // Left/Right switch adapter views; with a single view they keep their usual focus movement.
            case Key.Left or Key.Right when _views.CanSwitch && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                SwitchView(next: e.Key == Key.Right);
                break;
        }
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
