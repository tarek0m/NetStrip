namespace NetStrip.Core.Layout;

/// <summary>
/// Decides when repeated taskbar-attach failures switch the strip to floating mode (spec §6 Fallback).
/// Failures inside the startup grace window (e.g. login before Explorer has created the taskbar) are not counted.
/// <c>sinceStart</c> is measured by the caller from its own reference point (app start, or the last Explorer restart).
/// Not thread-safe; use from the UI thread.
/// </summary>
public sealed class AttachRetryPolicy(TimeSpan startupGrace, int maxFailures)
{
    private static readonly TimeSpan FastRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FallbackRetry = TimeSpan.FromSeconds(60);

    private int _consecutiveFailures;

    public bool ShouldFallBack => _consecutiveFailures >= maxFailures;

    public void RecordFailure(TimeSpan sinceStart)
    {
        if (sinceStart < startupGrace) return;
        if (_consecutiveFailures < int.MaxValue) _consecutiveFailures++;
    }

    public void RecordSuccess() => _consecutiveFailures = 0;

    /// <summary>1 s while waiting (during grace, or after grace before fallback); 60 s once in fallback.</summary>
    public TimeSpan NextRetryDelay(TimeSpan sinceStart) =>
        ShouldFallBack && sinceStart >= startupGrace ? FallbackRetry : FastRetry;
}
