namespace NetStrip.Core.Usage;

/// <summary>
/// On-disk shape of usage.json (§5): <c>{ "version":1, "adapters": { "&lt;id&gt;": { "2026-10-23": [up, down] } },
/// "capFired": { "2026-10-01": ["Warning80"] } }</c>. Serialized with camelCase names.
/// </summary>
internal sealed class UsageFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, Dictionary<string, long[]>>? Adapters { get; set; }

    /// <summary>Keyed by the first day of the cap period; values are <see cref="CapLevel"/> names.</summary>
    public Dictionary<string, List<string>>? CapFired { get; set; }
}
