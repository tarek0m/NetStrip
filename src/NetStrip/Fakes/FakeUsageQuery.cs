using NetStrip.Core;

namespace NetStrip.Fakes;

/// <summary>Temporary canned usage numbers for <c>--fake</c> runs (replaced by UsageStore in Task 13).</summary>
public sealed class FakeUsageQuery(ISettingsStore settings) : IUsageQuery
{
    public Totals Today(string scope) => new(210L * 1024 * 1024, 1_240L * 1024 * 1024);
    public Totals ThisPeriod(string scope) => new(3L * 1024 * 1024 * 1024, 18L * 1024 * 1024 * 1024);

    public (long Used, long Cap)? CapProgress() =>
        settings.Current.CapGb is { } gb and > 0
            ? ((long)(21.3 * 1024 * 1024 * 1024), (long)(gb * 1024 * 1024 * 1024))
            : null;

    public string? LoadNotice => null;
}
