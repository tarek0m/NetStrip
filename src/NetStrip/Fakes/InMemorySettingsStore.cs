using NetStrip.Core;

namespace NetStrip.Fakes;

/// <summary>Temporary in-memory settings for <c>--fake</c> runs (replaced by the real SettingsStore in Task 13).</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Current { get; } = new() { StartWithWindows = false };

    public event Action<AppSettings>? Changed;

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Changed?.Invoke(Current);
    }
}
