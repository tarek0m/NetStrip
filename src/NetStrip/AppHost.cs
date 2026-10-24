using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using NetStrip.Core;
using NetStrip.Core.Net;
using NetStrip.Core.Presentation;
using NetStrip.Core.Settings;
using NetStrip.Core.Updates;
using NetStrip.Core.Usage;
using NetStrip.Fakes;
using NetStrip.Flyout;
using NetStrip.Platform;
using NetStrip.Settings;
using NetStrip.Shell.Fullscreen;
using NetStrip.Shell.Net;
using NetStrip.Shell.Taskbar;
using NetStrip.Strip;
using NetStrip.Theme;

namespace NetStrip;

/// <summary>
/// Composition root: owns every component's lifetime, routes each sample to usage, strips, graph and the fullscreen
/// watcher, pushes settings changes to their consumers, and tears everything down in a fixed order.
/// UI-thread only; the sampler's event is marshalled to the dispatcher.
/// </summary>
internal sealed class AppHost : IDisposable
{
    internal const string FallbackNotice =
        "NetStrip couldn't attach to the taskbar, so it's floating for now. It will retry automatically.";
    private const string FakeNotice = "Running with simulated traffic (--fake).";
    private const string PrimaryKey = "primary";   // TaskbarKeys.Primary (internal to NetStrip.Shell)

    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(60);
    // Ticks hourly; UpdateChecker itself allows at most one request per 24 h, so drift or sleep never skips a day.
    private static readonly TimeSpan UpdateTimerInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FlyoutReopenGuard = TimeSpan.FromMilliseconds(250);
    // Working-set trims: once startup has settled (attach retries included), and after transient windows close.
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(10);
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient());

    private readonly App _app;
    private readonly ILog _log;
    private readonly bool _fake;
    private readonly Dispatcher _dispatcher;
    private readonly Action<Sample> _onSampleUi;
    private readonly GraphBuffer _graph = new();
    private readonly List<string> _pendingNotices = [];
    private readonly CancellationTokenSource _cts = new();

    private ThemeColors _theme = null!;
    private DeferredTheme _themeMode = null!;
    private SettingsStore _settings = null!;
    private UsageStore _usage = null!;
    private Win32InterfaceTable? _table;
    private NetSampler? _sampler;
    private FakeSampleSource? _fakeSource;
    private ISampleSource _source = null!;
    private FullscreenWatcher _fullscreen = null!;
    private TaskbarHost _host = null!;
    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _trimTimer;
    private UpdateChecker? _updates;
    private TrayNotification? _notification;

    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private UpdateInfo? _update;
    private Sample? _lastSample;
    private string? _fullscreenKey;
    private bool _hideInFullscreen;
    private bool _paused;
    private bool _checkingUpdates;
    private bool _sampleErrorLogged;
    private long _flyoutHiddenAt;
    private bool _started;
    private bool _disposed;

    public AppHost(App app, ILog log, bool fake)
    {
        _app = app;
        _log = log;
        _fake = fake;
        _dispatcher = app.Dispatcher;
        _onSampleUi = OnSampleUi;
    }

    private static string RepoSlug =>
        typeof(AppHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoSlug")?.Value ?? "";

    private static Version CurrentVersion => typeof(AppHost).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Builds and starts everything. Call once, on the UI thread.</summary>
    public void Start()
    {
        if (_started) throw new InvalidOperationException("AppHost already started.");
        _started = true;

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetStrip");
        var clock = new SystemClock();

        // 1. Settings, usage, sampler.
        _settings = new SettingsStore(Path.Combine(dataDir, "settings.json"), new RealFileSystem(), _log, clock);
        var cfg = _settings.Load();

        IFileSystem usageFs = _fake ? new InMemoryFileSystem() : new RealFileSystem();
        _usage = new UsageStore(Path.Combine(dataDir, "usage.json"), clock, usageFs, _log);
        _usage.Load();
        ApplyCapSettings(cfg);

        if (_fake)
        {
            _fakeSource = new FakeSampleSource(clock);
            _source = _fakeSource;
        }
        else
        {
            _table = new Win32InterfaceTable(_log);
            _sampler = new NetSampler(_table, clock, _log) { AdapterSetting = cfg.Adapter };
            _source = _sampler;
        }

        _theme = new ThemeColors();
        _theme.StartListening();
        // Only remembered: the Fluent theme loads when Fluent-styled UI first appears (EnsureTheme).
        _themeMode = new DeferredTheme(cfg.Theme, ApplyThemeMode);
        _hideInFullscreen = cfg.HideInFullscreen;

        _fullscreen = new FullscreenWatcher(_log);
        _fullscreen.FullscreenChanged += OnFullscreenChanged;

        // 2. Taskbar host and its strips.
        _host = new TaskbarHost(CreateStrip, _log) { Scope = cfg.Monitors };
        _host.FallbackActivated += OnFallbackActivated;

        // 5. Notices, cap warnings.
        _usage.CapThresholdCrossed += OnCapThresholdCrossed;
        if (_usage.LoadNotice is { } notice) AddNotice(notice);
        if (_fake) AddNotice(FakeNotice);

        // 7. Settings and activation.
        _settings.Changed += OnSettingsChanged;
        _app.ActivationRequested += OnActivationRequested;

        // 6. Persistence every 60 s (Exit and SessionEnding save through App).
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = SaveInterval };
        _saveTimer.Tick += (_, _) => SaveUsage();
        _saveTimer.Start();

        // 8. Update check at startup and then daily.
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = UpdateTimerInterval };
        _updateTimer.Tick += (_, _) => CheckForUpdates();
        _updateTimer.Start();

        _trimTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, _dispatcher);
        _trimTimer.Tick += (_, _) => TrimWorkingSet();
        ScheduleTrim(StartupTrimDelay);

        _host.Start();

        // 3. Samples last, so every consumer exists before the first one arrives.
        _source.SampleProduced += OnSampleProduced;
        _sampler?.Start();
        _fakeSource?.Start();

        CheckForUpdates();
        _log.Info($"AppHost started (version {CurrentVersion}, fake={_fake}, scope={cfg.Monitors}, adapter={cfg.Adapter}).");
    }

    // ---- Strips ---------------------------------------------------------------------------------------

    private IStripSurface CreateStrip(string key)
    {
        var strip = new StripWindow(_theme);
        strip.FlyoutRequested += () => ToggleFlyout(strip);
        strip.SettingsRequested += ShowSettings;
        strip.ExitRequested += RequestExit;
        strip.PauseToggled += SetPaused;
        strip.ThemeNeeded += EnsureTheme;
        strip.SetPaused(_paused);
        if (key == PrimaryKey)
        {
            strip.FloatingPosition = _settings.Current.FloatingPosition;
            strip.FloatingMoved += OnFloatingMoved;
        }
        if (_hideInFullscreen && key == _fullscreenKey) strip.SetHidden(true);
        if (_lastSample is { } s)
            strip.Show(s, _usage.Today(ScopeFor(_settings.Current, s)), _usage.CapProgress(), _settings.Current.Units, _settings.Current.IdleFade);
        return strip;
    }

    private IEnumerable<StripWindow> Strips()
    {
        foreach (var surface in _host.Surfaces.Values)
            if (surface is StripWindow strip) yield return strip;
    }

    private static string ScopeFor(AppSettings cfg, Sample s) =>
        string.Equals(cfg.Adapter, AdapterIds.Auto, StringComparison.OrdinalIgnoreCase) ? s.AdapterId : cfg.Adapter;

    // ---- Samples --------------------------------------------------------------------------------------

    /// <summary>Sampler thread: hand the sample to the UI thread.</summary>
    private void OnSampleProduced(Sample s) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, _onSampleUi, s);

    private void OnSampleUi(Sample s)
    {
        if (_disposed) return;
        try
        {
            // Usage and the cap keep counting while the speed display is paused; only the display freezes.
            _usage.Add(s);
            _fullscreen.Poll();
            if (!_paused)
            {
                _lastSample = s;
                var cfg = _settings.Current;
                var today = _usage.Today(ScopeFor(cfg, s));
                var cap = _usage.CapProgress();
                foreach (var surface in _host.Surfaces.Values)
                {
                    if (surface is StripWindow strip) strip.Show(s, today, cap, cfg.Units, cfg.IdleFade);
                }
                _graph.Push(s);
            }
            _sampleErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (!_sampleErrorLogged) _log.Error("Handling a sample failed", ex);
            _sampleErrorLogged = true;
        }
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        if (_flyout is not null) _flyout.SpeedsPaused = paused;
        foreach (var strip in Strips()) strip.SetPaused(paused);
        _log.Info(paused ? "Speed display paused (usage keeps counting)" : "Speed display resumed");
    }

    // ---- Fullscreen, fallback, cap ----------------------------------------------------------------------

    private void OnFullscreenChanged(string key, bool fullscreen)
    {
        if (fullscreen) _fullscreenKey = key;
        else if (_fullscreenKey == key) _fullscreenKey = null;
        if (_hideInFullscreen && _host.Surfaces.TryGetValue(key, out var surface)) surface.SetHidden(fullscreen);
    }

    /// <summary>Re-applies fullscreen hiding to every strip (when the setting changes).</summary>
    private void ApplyFullscreenHiding()
    {
        foreach (var (key, surface) in _host.Surfaces)
            surface.SetHidden(_hideInFullscreen && key == _fullscreenKey);
    }

    private void OnFallbackActivated() => AddNotice(FallbackNotice);

    private void OnCapThresholdCrossed(CapLevel level)
    {
        // Raised from inside UsageStore.Add: nothing may escape back into the sample chain.
        try
        {
            _log.Info($"Data cap threshold crossed: {level}");
            if (_usage.CapProgress() is not { } progress ||
                CapWarning.Message(level, progress.Used, progress.Cap) is not { } message) return;
            // The notice keeps the warning in the flyout in case the Windows notification is missed.
            AddNotice(message);
            ShowNotification(message);
        }
        catch (Exception ex)
        {
            _log.Error("Showing the data cap warning failed", ex);
        }
    }

    /// <summary>Windows notification; clicking it opens the flyout.</summary>
    private void ShowNotification(string message)
    {
        if (_notification is null)
        {
            _notification = new TrayNotification(_log);
            _notification.Clicked += () =>
            {
                if (!_disposed) ShowFlyout(null);
            };
        }
        _notification.Show("NetStrip", message);
    }

    // ---- Settings -----------------------------------------------------------------------------------------

    private void OnSettingsChanged(AppSettings cfg)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsChanged(_settings.Current));
            return;
        }
        if (_disposed) return;

        try
        {
            if (_sampler is not null) _sampler.AdapterSetting = cfg.Adapter;
            _host.Scope = cfg.Monitors;
            ApplyCapSettings(cfg);
            _themeMode.Set(cfg.Theme);

            if (_hideInFullscreen != cfg.HideInFullscreen)
            {
                _hideInFullscreen = cfg.HideInFullscreen;
                ApplyFullscreenHiding();
            }

            foreach (var (key, surface) in _host.Surfaces)
            {
                if (surface is not StripWindow strip) continue;
                if (key == PrimaryKey) strip.FloatingPosition = cfg.FloatingPosition;
                // Units, idle fade and cap changes show immediately instead of on the next tick.
                if (_lastSample is { } s) strip.Show(s, _usage.Today(ScopeFor(cfg, s)), _usage.CapProgress(), cfg.Units, cfg.IdleFade);
            }

            if (!cfg.CheckForUpdates && _update is not null)
            {
                _update = null;
                _flyout?.SetUpdate(null);
            }
            else if (cfg.CheckForUpdates)
            {
                CheckForUpdates();   // no-op within 24 h of the last check
            }

            _host.Reposition();
        }
        catch (Exception ex)
        {
            _log.Error("Applying settings failed", ex);
        }
    }

    private void ApplyCapSettings(AppSettings cfg)
    {
        _usage.CapGb = cfg.CapGb;
        _usage.CapResetDay = cfg.CapResetDay;
        _usage.CapScope = cfg.CapAdapter;
    }

    /// <summary>
    /// Loads the Fluent theme (<see cref="Application.ThemeMode"/>) the first time Fluent-styled UI is about to appear:
    /// the flyout, the settings window, or the strip's menu or tooltip. The strip itself draws with ThemeColors, so
    /// until then PresentationFramework.Fluent stays unloaded. Synchronous: callers rely on the styles existing (and
    /// Mica.IsAppDark seeing the right mode) when it returns. Later calls are no-ops.
    /// </summary>
    private void EnsureTheme(string trigger)
    {
        if (_disposed || _themeMode.IsApplied) return;
        var started = Stopwatch.GetTimestamp();
        try
        {
            _themeMode.Ensure();
            _log.Info($"Fluent theme loaded ({_themeMode.Desired}) for the {trigger} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        }
        catch (Exception ex)
        {
            // Not retried (DeferredTheme marks it applied first); the window still opens, just without Fluent styles.
            _log.Error($"Loading the Fluent theme for the {trigger} failed", ex);
        }
    }

    private void ApplyThemeMode(ThemeSetting theme)
    {
        var mode = theme switch
        {
            ThemeSetting.Light => ThemeMode.Light,
            ThemeSetting.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (_app.ThemeMode != mode) _app.ThemeMode = mode;
    }

    private void OnFloatingMoved(PixelPoint position)
    {
        _log.Info($"Floating strip moved to ({position.X}, {position.Y})");
        _settings.Update(s => s.FloatingPosition = position);
    }

    // ---- Flyout and windows ---------------------------------------------------------------------------

    /// <summary>The flyout is created on first use; notices and update info wait here until then.</summary>
    private FlyoutWindow GetFlyout()
    {
        if (_flyout is not null) return _flyout;
        // Before construction: the flyout's DynamicResources, Window style and Mica tint all read the theme.
        EnsureTheme("flyout");
        var flyout = new FlyoutWindow(_source, _graph, _usage, _settings, _theme);
        flyout.SettingsRequested += ShowSettings;
        flyout.IsVisibleChanged += OnFlyoutVisibleChanged;
        foreach (var notice in _pendingNotices) flyout.AddNotice(notice);
        _pendingNotices.Clear();
        flyout.SetUpdate(_update);
        flyout.SpeedsPaused = _paused;
        return _flyout = flyout;
    }

    private void AddNotice(string notice)
    {
        _log.Info($"Notice: {notice}");
        if (_flyout is not null) _flyout.AddNotice(notice);
        else if (!_pendingNotices.Contains(notice)) _pendingNotices.Add(notice);
    }

    private void OnFlyoutVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not false) return;
        _flyoutHiddenAt = Stopwatch.GetTimestamp();
        ScheduleTrim(IdleTrimDelay);
    }

    private void ToggleFlyout(StripWindow clicked)
    {
        if (_disposed) return;
        if (_flyout is { IsOpen: true })
        {
            _flyout.Hide();
            return;
        }
        // Clicking the strip first deactivates (and so hides) an open flyout; that same click must not reopen it.
        if (_flyoutHiddenAt != 0 && Stopwatch.GetElapsedTime(_flyoutHiddenAt) < FlyoutReopenGuard) return;
        ShowFlyout(clicked);
    }

    private void OnActivationRequested()
    {
        if (_disposed) return;
        _log.Info("Activation requested by a second instance; opening the flyout");
        ShowFlyout(null);
    }

    private void ShowFlyout(StripWindow? anchor)
    {
        try
        {
            var stripRect = AnchorRect(anchor);
            GetFlyout().ShowAt(stripRect, Monitors.TaskbarEdgeNear(stripRect));
        }
        catch (Exception ex)
        {
            _log.Error("Opening the flyout failed", ex);
        }
    }

    /// <summary>The clicked strip, else the primary (or floating) one, else any visible strip, else the primary tray corner.</summary>
    private PixelRect AnchorRect(StripWindow? preferred)
    {
        if (preferred is { IsVisible: true }) return preferred.ScreenBounds;
        if (_host.Surfaces.TryGetValue(PrimaryKey, out var primary) && primary is StripWindow { IsVisible: true } p)
            return p.ScreenBounds;
        foreach (var strip in Strips())
            if (strip.IsVisible) return strip.ScreenBounds;

        var work = Monitors.WorkAreaNear(new PixelRect(0, 0, 1, 1));
        return Monitors.TaskbarEdgeNear(work) switch
        {
            TaskbarEdge.Top => new PixelRect(work.X + work.Width - 220, work.Y - 40, 200, 40),
            TaskbarEdge.Left => new PixelRect(work.X - 40, work.Y + work.Height - 60, 40, 40),
            TaskbarEdge.Right => new PixelRect(work.X + work.Width, work.Y + work.Height - 60, 40, 40),
            _ => new PixelRect(work.X + work.Width - 220, work.Y + work.Height, 200, 40),
        };
    }

    private void ShowSettings()
    {
        if (_disposed) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        // Before construction: SettingsWindow.xaml resolves the Fluent styles it is BasedOn with StaticResource, once.
        EnsureTheme("settings window");
        _settingsWindow = new SettingsWindow(_settings, _source.Adapters);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ScheduleTrim(IdleTrimDelay);
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void RequestExit()
    {
        _log.Info("Exit requested");
        // Let the menu click finish before windows (including the strip that raised this) are closed.
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _app.Shutdown());
    }

    // ---- Updates and persistence ------------------------------------------------------------------------

    private async void CheckForUpdates()
    {
        if (_disposed || _checkingUpdates || !_settings.Current.CheckForUpdates) return;
        var slug = RepoSlug;
        if (string.IsNullOrWhiteSpace(slug)) return;

        _checkingUpdates = true;
        try
        {
            _updates ??= new UpdateChecker(SharedHttp.Value, slug, CurrentVersion, new SystemClock(), _log);
            var result = await _updates.CheckAsync(_cts.Token);
            // null means "no update" or "checked within the last 24 h": keep whatever is already shown.
            if (result is not null && !_disposed && _settings.Current.CheckForUpdates)
            {
                _log.Info($"New version available: {result.Version} ({result.ReleaseUrl})");
                _update = result;
                _flyout?.SetUpdate(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _log.Warn("Update check failed", ex);
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    // ---- Memory -------------------------------------------------------------------------------------------

    /// <summary>(Re)starts the one-shot trim timer; a later request replaces an earlier one.</summary>
    private void ScheduleTrim(TimeSpan delay)
    {
        if (_trimTimer is null || _disposed) return;
        _trimTimer.Stop();
        _trimTimer.Interval = delay;
        _trimTimer.Start();
    }

    private void TrimWorkingSet()
    {
        _trimTimer?.Stop();
        // An open window is in use; closing it schedules the next trim.
        if (_disposed || _flyout is { IsOpen: true } || _settingsWindow is not null) return;
        try
        {
            if (!WorkingSet.Trim()) _log.Warn("Trimming the working set failed");
        }
        catch (Exception ex)
        {
            _log.Warn("Trimming the working set failed", ex);
        }
    }

    /// <summary>Writes usage.json if anything changed. Safe to call at any time, including during shutdown.</summary>
    public void SaveUsage()
    {
        try
        {
            _usage?.Save();
        }
        catch (Exception ex)
        {
            _log.Error("Saving usage failed", ex);
        }
    }

    // ---- Shutdown -------------------------------------------------------------------------------------

    /// <summary>Order: UI and strips (via TaskbarHost) → sampler → usage save. App then flushes the log and releases the single-instance handle.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _saveTimer?.Stop();
        _updateTimer?.Stop();
        _trimTimer?.Stop();
        _cts.Cancel();

        if (_source is not null) _source.SampleProduced -= OnSampleProduced;
        if (_settings is not null) _settings.Changed -= OnSettingsChanged;
        if (_usage is not null) _usage.CapThresholdCrossed -= OnCapThresholdCrossed;
        if (_fullscreen is not null) _fullscreen.FullscreenChanged -= OnFullscreenChanged;
        _app.ActivationRequested -= OnActivationRequested;

        Try("closing the settings window", () => _settingsWindow?.Close());
        Try("closing the flyout", () => _flyout?.Close());
        Try("removing the notification icon", () => _notification?.Dispose());
        if (_host is not null)
        {
            _host.FallbackActivated -= OnFallbackActivated;
            Try("disposing the taskbar host", _host.Dispose);
        }
        Try("stopping the sampler", () => _sampler?.Dispose());
        Try("stopping the fake source", () => _fakeSource?.Dispose());
        Try("disposing the interface table", () => _table?.Dispose());
        SaveUsage();
        Try("disposing the theme listener", () => _theme?.Dispose());
        _cts.Dispose();
    }

    private void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Warn($"Shutdown: {what} failed", ex);
        }
    }
}
