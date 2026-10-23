using System.Windows;
using NetStrip.Core;
using NetStrip.Core.Presentation;
using NetStrip.Platform;
using NetStrip.Strip;
using NetStrip.Theme;

namespace NetStrip.Fakes;

/// <summary>
/// Temporary <c>--fake</c> composition (Task 13 replaces it with AppHost): synthetic traffic,
/// in-memory settings/usage and, with <c>--floating</c>, a floating strip.
/// </summary>
internal sealed class FakeSession : IDisposable
{
    private readonly App _app;
    private readonly FakeSampleSource _source = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeUsageQuery _usage;
    private readonly StripWindow? _strip;

    public FakeSession(App app, ThemeColors theme, bool floating)
    {
        _app = app;
        _usage = new FakeUsageQuery(_settings);

        if (floating)
        {
            _strip = new StripWindow(theme);
            _strip.PauseToggled += paused => _source.Paused = paused;
            _strip.ExitRequested += () => _app.Shutdown();
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
        var pos = _settings.Current.FloatingPosition
                  ?? new PixelPoint(work.X + work.Width - w - (int)(120 * dpi), work.Y + work.Height - h - (int)(12 * dpi));
        strip.Apply(new StripPlacement(0, new PixelRect(pos.X, pos.Y, w, h), StripMode.Floating, dpi));
    }

    private void OnSample(Sample s) =>
        _app.Dispatcher.BeginInvoke(() =>
        {
            var cfg = _settings.Current;
            _strip?.Show(s, _usage.Today(AdapterIds.All), cfg.Units, cfg.IdleFade);
        });

    public void Dispose()
    {
        _source.SampleProduced -= OnSample;
        _source.Dispose();
        _strip?.Close();
    }
}
