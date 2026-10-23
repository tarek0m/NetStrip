namespace NetStrip.Core.Presentation;

/// <summary>
/// Synthetic sine-wave traffic for <c>--fake</c> runs. Every 30 s the pattern drops to zero for 8 s so the idle fade can be observed.
/// Samples are raised on a thread-pool thread; consumers marshal to the UI thread.
/// </summary>
public sealed class FakeSampleSource : ISampleSource, IDisposable
{
    public const string FakeAdapterId = "fake-adapter";
    private const int CycleSeconds = 30;
    private const int QuietSeconds = 8;

    private static readonly AdapterInfo Adapter = new(FakeAdapterId, "Fake Ethernet", AdapterKind.Ethernet, "192.168.1.42");

    private readonly IClock _clock;
    private readonly Timer _timer;
    private int _second;

    public FakeSampleSource(IClock? clock = null)
    {
        _clock = clock ?? new SystemClock();
        Adapters = [Adapter];
        // Created stopped; Start() arms it so tests can drive Tick() deterministically.
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<Sample>? SampleProduced;
    public AdapterInfo? CurrentAdapter => Adapter;
    public IReadOnlyList<AdapterInfo> Adapters { get; }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    /// <summary>Produces the next sample now.</summary>
    public void Tick()
    {
        var second = Interlocked.Increment(ref _second);
        SampleProduced?.Invoke(PatternAt(second, FakeAdapterId) with { Time = _clock.Now });
    }

    /// <summary>The deterministic traffic pattern at a given second of the run.</summary>
    public static Sample PatternAt(int second, string adapterId)
    {
        var time = DateTimeOffset.UnixEpoch.AddSeconds(second);
        if (second % CycleSeconds >= CycleSeconds - QuietSeconds)
            return new Sample(time, adapterId, 0, 0);

        var down = 20_000 + 6_000_000 * (0.5 + 0.5 * Math.Sin(second * 0.35));
        var up = 3_000 + 500_000 * (0.5 + 0.5 * Math.Sin(second * 0.5 + 1.0));
        return new Sample(time, adapterId, (long)up, (long)down);
    }

    public void Dispose() => _timer.Dispose();
}
