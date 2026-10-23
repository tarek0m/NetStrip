namespace NetStrip.Core.Net;

public static class AdapterSelection
{
    /// <summary>
    /// Picks the counters a sample is computed from.
    /// <c>all</c>: every included adapter. <c>auto</c>: the best-route adapter when it is present and included,
    /// otherwise every included adapter. Anything else is an adapter id: the matching included adapter, or an
    /// empty selection when it is missing (the returned id is then the requested one).
    /// </summary>
    /// <param name="buffer">Optional list to fill and return, so a per-second caller does not allocate.</param>
    public static (IReadOnlyList<InterfaceCounters> chosen, string adapterId) Resolve(
        IReadOnlyList<InterfaceCounters> counters, string setting, string? bestId, List<InterfaceCounters>? buffer = null)
    {
        var chosen = buffer ?? new List<InterfaceCounters>();
        chosen.Clear();

        if (string.Equals(setting, AdapterIds.Auto, StringComparison.OrdinalIgnoreCase))
        {
            if (bestId is not null && TryAddById(counters, bestId, chosen))
                return (chosen, bestId);
            AddAllIncluded(counters, chosen);
            return (chosen, AdapterIds.All);
        }

        if (string.Equals(setting, AdapterIds.All, StringComparison.OrdinalIgnoreCase))
        {
            AddAllIncluded(counters, chosen);
            return (chosen, AdapterIds.All);
        }

        TryAddById(counters, setting, chosen);
        return (chosen, setting);
    }

    private static bool TryAddById(IReadOnlyList<InterfaceCounters> counters, string id, List<InterfaceCounters> into)
    {
        for (var i = 0; i < counters.Count; i++)
        {
            var c = counters[i];
            if (c.Included && string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                into.Add(c);
                return true;
            }
        }
        return false;
    }

    private static void AddAllIncluded(IReadOnlyList<InterfaceCounters> counters, List<InterfaceCounters> into)
    {
        for (var i = 0; i < counters.Count; i++)
            if (counters[i].Included) into.Add(counters[i]);
    }
}
