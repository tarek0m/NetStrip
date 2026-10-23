using NetStrip.Core;
using NetStrip.Core.Presentation;
using NetStrip.Flyout;
using NetStrip.Platform;
using NetStrip.Settings;
using NetStrip.Strip;
using NetStrip.Theme;

namespace NetStrip.Fakes;

/// <summary>
/// Temporary <c>--fake</c> composition (Task 13 replaces it with AppHost): synthetic traffic,
/// in-memory settings/usage, the flyout and settings window and, with <c>--floating</c>, a floating strip.
/// </summary>
internal sealed class FakeSession : IDisposable
{
    private readonly App _app;
    private readonly FakeSampleSource _source = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeUsageQuery _usage;
    private readonly GraphBuffer _graph = new();
    private readonly FlyoutWindow _flyout;
    private readonly StripWindow? _strip;
    private SettingsWindow? _settingsWindow;

    public FakeSession(App app, ThemeColors theme, bool floating)
    {
        _app = app;
        _settings.Current.CapGb = 25;   // makes the cap bar (and its warning colour) visible
        _usage = new FakeUsageQuery(_settings);

        _flyout = new FlyoutWindow(_source, _graph, _usage, _settings, theme);
        _flyout.SettingsRequested += ShowSettings;
        _flyout.AddNotice("Running with simulated traffic (--fake).");
        _app.ActivationRequested += ShowFlyout;

        if (floating)
        {
            _strip = new StripWindow(theme);
            _strip.PauseToggled += paused => _source.Paused = paused;
            _strip.ExitRequested += () => _app.Shutdown();
            _strip.SettingsRequested += ShowSettings;
            _strip.FlyoutRequested += ToggleFlyout;
            _strip.FloatingMoved += p => _settings.Update(s => s.FloatingPosition = p);
            PlaceFloating();
        }

        _source.SampleProduced += OnSample;
        _source.Start();
    }

    private void PlaceFloating()
    {
        var strip = _strip!;
        var work = Monitors.WorkAreaNear(new PixelRect(0, 0, 1, 1));
        var dpi = Monitors.DpiScaleNear(new PixelRect(0, 0, 1, 1));
        var (w, h) = strip.Measure(StripMode.Floating, dpi, 0);
        App.Log.Info($"Strip measure at {dpi:F2}x: full={strip.Measure(StripMode.Full, dpi, 48)}, compact={strip.Measure(StripMode.Compact, dpi, 48)}, floating={(w, h)}");
        var pos = _settings.Current.FloatingPosition
                  ?? new PixelPoint(work.X + work.Width - w - (int)(120 * dpi), work.Y + work.Height - h - (int)(12 * dpi));
        strip.Apply(new StripPlacement(0, new PixelRect(pos.X, pos.Y, w, h), StripMode.Floating, dpi));
    }

    private void ToggleFlyout()
    {
        if (_flyout.IsOpen) _flyout.Hide();
        else ShowFlyout();
    }

    private void ShowFlyout()
    {
        if (_strip is { IsVisible: true })
        {
            _flyout.ShowAt(_strip.ScreenBounds, TaskbarEdge.Bottom);
            return;
        }
        // No strip (plain --fake): pretend one sits at the bottom-right of the work area.
        var work = Monitors.WorkAreaNear(new PixelRect(0, 0, 1, 1));
        _flyout.ShowAt(new PixelRect(work.X + work.Width - 220, work.Y + work.Height, 200, 40), TaskbarEdge.Bottom);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _source.Adapters);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnSample(Sample s) =>
        _app.Dispatcher.BeginInvoke(() =>
        {
            var cfg = _settings.Current;
            _graph.Push(s);
            _strip?.Show(s, _usage.Today(AdapterIds.All), cfg.Units, cfg.IdleFade);
        });

    public void Dispose()
    {
        _app.ActivationRequested -= ShowFlyout;
        _source.SampleProduced -= OnSample;
        _source.Dispose();
        _settingsWindow?.Close();
        _flyout.Close();
        _strip?.Dispose();
    }
}
