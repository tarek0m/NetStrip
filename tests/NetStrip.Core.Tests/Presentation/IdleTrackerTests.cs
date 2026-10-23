using NetStrip.Core.Presentation;

namespace NetStrip.Core.Tests.Presentation;

public class IdleTrackerTests
{
    private static Sample S(long up, long down) => new(DateTimeOffset.UnixEpoch, "a", up, down);

    [Fact]
    public void Idle_After5QuietSamples()
    {
        var t = new IdleTracker();
        for (var i = 0; i < 4; i++) Assert.False(t.Update(S(0, 0)));
        Assert.True(t.Update(S(0, 0)));
        Assert.True(t.IsIdle);
    }

    [Fact]
    public void Idle_ResetsOnActivity()
    {
        var t = new IdleTracker();
        for (var i = 0; i < 6; i++) t.Update(S(0, 0));
        Assert.True(t.IsIdle);
        Assert.False(t.Update(S(5000, 0)));
        Assert.False(t.IsIdle);
        for (var i = 0; i < 4; i++) Assert.False(t.Update(S(0, 0)));
        Assert.True(t.Update(S(0, 0)));
    }

    [Fact]
    public void Idle_ThresholdBoundary_1023IsQuiet_1024IsActive()
    {
        var quiet = new IdleTracker();
        for (var i = 0; i < 5; i++) quiet.Update(S(1023, 1023));
        Assert.True(quiet.IsIdle);

        var activeUp = new IdleTracker();
        for (var i = 0; i < 5; i++) activeUp.Update(S(1024, 0));
        Assert.False(activeUp.IsIdle);

        var activeDown = new IdleTracker();
        for (var i = 0; i < 5; i++) activeDown.Update(S(0, 1024));
        Assert.False(activeDown.IsIdle);
    }
}
