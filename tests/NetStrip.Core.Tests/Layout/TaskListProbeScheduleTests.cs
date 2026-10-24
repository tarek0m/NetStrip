using NetStrip.Core.Layout;

namespace NetStrip.Core.Tests.Layout;

public class TaskListProbeScheduleTests
{
    private static TaskListProbeSchedule NewSchedule() => new(TimeSpan.FromSeconds(30));
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void DueBeforeAnyProbeHasCompleted()
    {
        var schedule = NewSchedule();
        Assert.True(schedule.IsDue(S(0)));
        Assert.True(schedule.IsDue(S(5)));
    }

    [Fact]
    public void WaitsTheIntervalAfterACompletedProbe()
    {
        var schedule = NewSchedule();
        schedule.RecordCompleted(S(10));

        Assert.False(schedule.IsDue(S(10)));
        Assert.False(schedule.IsDue(S(39.9)));
        Assert.True(schedule.IsDue(S(40)));
        Assert.True(schedule.IsDue(S(100)));
    }

    [Fact]
    public void AnyCompletionRestartsTheInterval()
    {
        // An attach-triggered probe at 35 s pushes the next periodic one back to 65 s.
        var schedule = NewSchedule();
        schedule.RecordCompleted(S(0));
        Assert.False(schedule.IsDue(S(29)));

        schedule.RecordCompleted(S(35));
        Assert.False(schedule.IsDue(S(40)));
        Assert.False(schedule.IsDue(S(64)));
        Assert.True(schedule.IsDue(S(65)));
    }

    [Fact]
    public void ClockGoingBackwardsIsNotDue()
    {
        var schedule = NewSchedule();
        schedule.RecordCompleted(S(50));
        Assert.False(schedule.IsDue(S(20)));
    }

    [Theory]
    [InlineData(false, StripMode.Full, true)]
    [InlineData(false, StripMode.Compact, true)]
    [InlineData(true, StripMode.Full, true)]
    [InlineData(true, StripMode.Compact, true)]
    [InlineData(false, StripMode.Vertical, false)]   // side taskbars never go compact; the tray is a window
    [InlineData(true, StripMode.Vertical, true)]     // the clock area of a secondary taskbar may only be in XAML
    [InlineData(false, StripMode.Floating, false)]
    [InlineData(true, StripMode.Floating, false)]
    public void WantsOnlyTaskbarsWhoseLayoutUsesTheProbe(bool secondary, StripMode mode, bool expected)
    {
        Assert.Equal(expected, TaskListProbeSchedule.Wants(secondary, mode));
    }
}
