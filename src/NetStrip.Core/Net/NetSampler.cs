using System.Runtime.InteropServices;

namespace NetStrip.Core.Net;

/// <summary>
/// Turns raw interface counters into one <see cref="Sample"/> per second (§4).
/// <see cref="Tick"/> is the pure step; <see cref="Start"/> drives it from a 1 s timer on a background task.
/// </summary>
public sealed class NetSampler(IInterfaceTable table, IClock clock, ILog log) : ISampleSource, IDisposable
{
    private const int AutoReevaluateTicks = 10;
    private const double MaxGapSeconds = 5.0;

    private readonly record struct Previous(ulong InOctets, ulong OutOctets, int Generation);

    private readonly Dictionary<string, Previous> _previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<InterfaceCounters> _chosen = new();
    private readonly object _lifecycle = new();

    private string _adapterSetting = AdapterIds.Auto;
    private volatile bool _forceBestLookup = true;
    private volatile IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();
    private volatile AdapterInfo? _currentAdapter;

    // Touched only by Tick (single caller at a time: the timer loop, or a test).
    private string? _bestId;
    private int _ticksSinceBestLookup = AutoReevaluateTicks;
    private bool _hasPreviousTick;
    private bool _adaptersBuilt;
    private long _previousTicks;
    private int _generation;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<Sample>? SampleProduced;

    /// <summary><c>"auto"</c>, <c>"all"</c> or an adapter id.</summary>
    public string AdapterSetting
    {
        get => Volatile.Read(ref _adapterSetting);
        set
        {
            Volatile.Write(ref _adapterSetting, value ?? AdapterIds.Auto);
            _forceBestLookup = true;
        }
    }

    public AdapterInfo? CurrentAdapter => _currentAdapter;

    public IReadOnlyList<AdapterInfo> Adapters => _adapters;

    public Sample Tick()
    {
        var nowTicks = clock.TimestampTicks;
        var counters = table.Read();
        var setting = AdapterSetting;

        string? bestId = null;
        if (string.Equals(setting, AdapterIds.Auto, StringComparison.OrdinalIgnoreCase))
        {
            if (_forceBestLookup || _ticksSinceBestLookup >= AutoReevaluateTicks)
            {
                _forceBestLookup = false;
                _bestId = table.BestInterfaceId();
                _ticksSinceBestLookup = 0;
            }
            _ticksSinceBestLookup++;
            bestId = _bestId;
        }

        var seconds = _hasPreviousTick ? (nowTicks - _previousTicks) / clock.TicksPerSecond : 0.0;
        var measurable = _hasPreviousTick && seconds > 0 && seconds <= MaxGapSeconds;

        var (chosen, adapterId) = AdapterSelection.Resolve(counters, setting, bestId, _chosen);

        ulong up = 0, down = 0;
        if (measurable)
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                if (TryDelta(chosen[i], out var u, out var d))
                {
                    up += u;
                    down += d;
                }
            }
        }

        // Every included adapter's bytes, for per-adapter usage. Bytes count even after a long gap (sleep): counters
        // are cumulative, so the delta is exact; only the rates need a measurable interval.
        var traffic = new List<AdapterTraffic>(counters.Count);
        for (var i = 0; i < counters.Count; i++)
        {
            var c = counters[i];
            if (!c.Included || !TryDelta(c, out var u, out var d)) continue;
            traffic.Add(new AdapterTraffic(c.Id, (long)u, (long)d,
                measurable ? (long)Math.Round(u / seconds) : 0,
                measurable ? (long)Math.Round(d / seconds) : 0));
        }

        UpdateBaselines(counters);
        UpdateAdapters(counters);
        _currentAdapter = adapterId == AdapterIds.All || chosen.Count == 0 ? null : FindAdapter(chosen[0].Id);

        _hasPreviousTick = true;
        _previousTicks = nowTicks;

        return new Sample(clock.Now, adapterId,
            measurable ? (long)Math.Round(up / seconds) : 0,
            measurable ? (long)Math.Round(down / seconds) : 0,
            traffic);
    }

    /// <summary>Bytes since the previous tick. False for a new adapter (no baseline) or a decreased counter (reset).</summary>
    private bool TryDelta(InterfaceCounters c, out ulong up, out ulong down)
    {
        if (_previous.TryGetValue(c.Id, out var prev) && c.InOctets >= prev.InOctets && c.OutOctets >= prev.OutOctets)
        {
            down = c.InOctets - prev.InOctets;
            up = c.OutOctets - prev.OutOctets;
            return true;
        }
        up = down = 0;
        return false;
    }

    /// <summary>One timer step: <see cref="Tick"/>, then raise <see cref="SampleProduced"/>.</summary>
    public Sample TickAndPublish()
    {
        var sample = Tick();
        SampleProduced?.Invoke(sample);
        return sample;
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickAndPublish(); }
                catch (Exception ex) { log.Error("Network sampling tick failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lifecycle)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        if (cts is null) return;

        cts.Cancel();
        try { loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        cts.Dispose();
    }

    private void UpdateBaselines(IReadOnlyList<InterfaceCounters> counters)
    {
        var generation = ++_generation;
        var included = 0;
        for (var i = 0; i < counters.Count; i++)
        {
            var c = counters[i];
            if (!c.Included) continue;
            included++;
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_previous, c.Id, out _);
            slot = new Previous(c.InOctets, c.OutOctets, generation);
        }

        // Adapters that vanished lose their baseline, so a returning adapter starts clean instead of spiking.
        if (_previous.Count != included)
        {
            foreach (var entry in _previous)
                if (entry.Value.Generation != generation) _previous.Remove(entry.Key);
        }
    }

    private void UpdateAdapters(IReadOnlyList<InterfaceCounters> counters)
    {
        var current = _adapters;
        var same = true;
        var index = 0;
        for (var i = 0; i < counters.Count && same; i++)
        {
            var c = counters[i];
            if (!c.Included) continue;
            same = index < current.Count && Matches(current[index], c);
            index++;
        }
        if (same && index == current.Count) return;

        var rebuilt = new List<AdapterInfo>(index);
        for (var i = 0; i < counters.Count; i++)
        {
            var c = counters[i];
            if (c.Included) rebuilt.Add(new AdapterInfo(c.Id, c.Name, c.Kind, c.Connected ? c.IPv4 : null, c.Connected));
        }
        // After the first build, a change means an adapter connected, disconnected or changed address: the best route
        // may have moved, so don't wait for the periodic re-evaluation.
        if (_adaptersBuilt) _forceBestLookup = true;
        _adaptersBuilt = true;
        _adapters = rebuilt;
    }

    private static bool Matches(AdapterInfo a, InterfaceCounters c) =>
        a.Id == c.Id && a.Name == c.Name && a.Kind == c.Kind && a.Connected == c.Connected
        && a.IPv4 == (c.Connected ? c.IPv4 : null);

    private AdapterInfo? FindAdapter(string id)
    {
        var list = _adapters;
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase)) return list[i];
        return null;
    }
}
