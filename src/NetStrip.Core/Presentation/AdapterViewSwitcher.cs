namespace NetStrip.Core.Presentation;

public enum AdapterViewKind { Configured, AllAdapters, Adapter }

/// <summary>
/// One page of the flyout's adapter switcher (§8). <see cref="AdapterViewKind.Configured"/> is what the strip shows
/// (the Adapter setting); the others show every adapter summed, or one adapter, without touching the settings.
/// </summary>
public sealed class AdapterView
{
    private AdapterView(AdapterViewKind kind, AdapterInfo? adapter)
    {
        Kind = kind;
        Adapter = adapter;
    }

    public static AdapterView Configured { get; } = new(AdapterViewKind.Configured, null);
    public static AdapterView AllAdapters { get; } = new(AdapterViewKind.AllAdapters, null);
    public static AdapterView ForAdapter(AdapterInfo adapter) => new(AdapterViewKind.Adapter, adapter);

    public AdapterViewKind Kind { get; }

    /// <summary>The adapter shown, as last listed by the sample source; set for <see cref="AdapterViewKind.Adapter"/> only.</summary>
    public AdapterInfo? Adapter { get; }

    /// <summary>Scope of the Today / This cycle totals: the adapter's id, otherwise every adapter (the configured view's totals).</summary>
    public string UsageScope => Adapter?.Id ?? AdapterIds.All;

    /// <summary>
    /// This view's rates in <paramref name="s"/>: the sample's own for the configured view, the sum of its
    /// <see cref="Sample.Traffic"/> for all adapters, or the matching adapter's (0 when it had no traffic entry).
    /// A sample without <see cref="Sample.Traffic"/> only knows its displayed adapter, so it counts as that one (as in usage).
    /// </summary>
    public (long Up, long Down) RatesFor(Sample s)
    {
        if (Kind == AdapterViewKind.Configured) return (s.UpBytesPerSec, s.DownBytesPerSec);

        if (s.Traffic is not { } traffic)
        {
            return Kind == AdapterViewKind.AllAdapters || IsAdapter(s.AdapterId)
                ? (s.UpBytesPerSec, s.DownBytesPerSec)
                : (0, 0);
        }

        long up = 0, down = 0;
        for (var i = 0; i < traffic.Count; i++)
        {
            var t = traffic[i];
            if (Kind == AdapterViewKind.AllAdapters)
            {
                up += t.UpBytesPerSec;
                down += t.DownBytesPerSec;
            }
            else if (IsAdapter(t.AdapterId))
            {
                return (t.UpBytesPerSec, t.DownBytesPerSec);
            }
        }
        return (up, down);
    }

    /// <summary>Largest up or down rate of this view over <paramref name="samples"/>, never below <see cref="GraphBuffer.MaxFloor"/> (the graph's scale).</summary>
    public long PeakRate(IReadOnlyList<Sample> samples)
    {
        var max = GraphBuffer.MaxFloor;
        for (var i = 0; i < samples.Count; i++)
        {
            var (up, down) = RatesFor(samples[i]);
            if (up > max) max = up;
            if (down > max) max = down;
        }
        return max;
    }

    /// <summary>Same page: same kind and, for an adapter view, the same adapter id (its name or address may have changed).</summary>
    public bool SameAs(AdapterView other) =>
        Kind == other.Kind && string.Equals(Adapter?.Id, other.Adapter?.Id, StringComparison.OrdinalIgnoreCase);

    private bool IsAdapter(string id) => string.Equals(Adapter?.Id, id, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The flyout's ◀ ▶ list of <see cref="AdapterView"/>s (§8): the configured view first, then All adapters, then each
/// included adapter (connected ones first, otherwise in the sample source's order). A view that would repeat the
/// configured one is left out, so a single adapter gives a single view. Moving wraps around at the ends; a rebuild
/// keeps the selection by adapter id and falls back to the configured view when it is gone. Not thread-safe.
/// </summary>
public sealed class AdapterViewSwitcher
{
    private List<AdapterView> _views = [AdapterView.Configured];
    private int _index;

    private string? _setting;
    private string? _displayedId;
    private IReadOnlyList<AdapterInfo>? _adapters;

    public IReadOnlyList<AdapterView> Views => _views;

    /// <summary>Position of <see cref="Current"/> in <see cref="Views"/>; 0 is the configured view.</summary>
    public int Index => _index;

    public AdapterView Current => _views[_index];

    /// <summary>More than one view: the arrows are worth showing.</summary>
    public bool CanSwitch => _views.Count > 1;

    /// <summary>
    /// Rebuilds the views; cheap when nothing changed (the sample source replaces its adapter list only on a change).
    /// </summary>
    /// <param name="adapterSetting">The Adapter setting: <c>"auto"</c>, <c>"all"</c> or an adapter id.</param>
    /// <param name="displayedAdapterId">The adapter the configured view shows right now (the best route in "auto"), or null.</param>
    /// <param name="adapters">Included adapters, in the sample source's order.</param>
    public void Update(string adapterSetting, string? displayedAdapterId, IReadOnlyList<AdapterInfo> adapters)
    {
        if (ReferenceEquals(adapters, _adapters)
            && string.Equals(adapterSetting, _setting, StringComparison.Ordinal)
            && string.Equals(displayedAdapterId, _displayedId, StringComparison.Ordinal))
            return;
        _setting = adapterSetting;
        _displayedId = displayedAdapterId;
        _adapters = adapters;

        // What the configured view really shows: "auto" is the best-route adapter, or every adapter without one.
        var configured = string.Equals(adapterSetting, AdapterIds.Auto, StringComparison.OrdinalIgnoreCase)
            ? displayedAdapterId ?? AdapterIds.All
            : adapterSetting;
        var configuredIsAll = string.Equals(configured, AdapterIds.All, StringComparison.OrdinalIgnoreCase);

        var views = new List<AdapterView>(adapters.Count + 2) { AdapterView.Configured };
        // With one adapter, "all" and that adapter are the same numbers.
        if (!configuredIsAll && adapters.Count >= 2) views.Add(AdapterView.AllAdapters);
        if (!(configuredIsAll && adapters.Count == 1))
        {
            foreach (var connected in (ReadOnlySpan<bool>)[true, false])
            {
                foreach (var a in adapters)
                {
                    if (a.Connected == connected && !string.Equals(a.Id, configured, StringComparison.OrdinalIgnoreCase))
                        views.Add(AdapterView.ForAdapter(a));
                }
            }
        }

        var selected = Current;
        _views = views;
        _index = Math.Max(0, views.FindIndex(v => v.SameAs(selected)));
    }

    /// <summary>Back to the configured view (each time the flyout opens).</summary>
    public void Reset() => _index = 0;

    /// <returns>True when the view changed.</returns>
    public bool Next() => Move(1);

    /// <returns>True when the view changed.</returns>
    public bool Previous() => Move(-1);

    private bool Move(int delta)
    {
        if (!CanSwitch) return false;
        _index = (_index + delta + _views.Count) % _views.Count;
        return true;
    }
}
