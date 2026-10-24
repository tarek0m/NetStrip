namespace NetStrip.Core.Layout;

/// <summary>
/// Decides which taskbars the task-list probe should look at and when the next periodic probe is due (spec §6
/// Centered-icon overlap). The app-button row on Windows 11's XAML taskbar grows without any HWND moving, so no
/// location event reports it; the host re-probes on a fixed cadence instead. Probes triggered by an attach pass are
/// not gated by this schedule, but their completion still counts, so a periodic probe never follows one closely.
/// <c>now</c> is any monotonic time the caller keeps (it must not restart with Explorer). Not thread-safe; use from
/// the UI thread.
/// </summary>
public sealed class TaskListProbeSchedule(TimeSpan interval)
{
    private TimeSpan? _lastCompleted;

    /// <summary>Minimum time between the end of one probe and the start of the next periodic one.</summary>
    public TimeSpan Interval => interval;

    /// <summary>
    /// Whether a taskbar's probe result can change where its strip goes: the app-button rect drives Full/Compact on
    /// horizontal taskbars, and secondary taskbars need the clock area whatever their edge. A vertical primary strip
    /// uses neither (its tray is a window, and side taskbars never switch to compact).
    /// </summary>
    /// <param name="secondary">The taskbar is not the primary one.</param>
    /// <param name="mode">The mode the strip is currently placed in on that taskbar.</param>
    public static bool Wants(bool secondary, StripMode mode) => mode switch
    {
        StripMode.Full or StripMode.Compact => true,
        StripMode.Vertical => secondary,
        _ => false,   // Floating: probe results are not used
    };

    /// <summary>True when no probe has completed yet, or the last one completed at least <see cref="Interval"/> ago.</summary>
    public bool IsDue(TimeSpan now) => _lastCompleted is not { } last || now - last >= interval;

    /// <summary>Records that a probe (periodic or not) finished at <paramref name="now"/>, successful or not.</summary>
    public void RecordCompleted(TimeSpan now) => _lastCompleted = now;
}
