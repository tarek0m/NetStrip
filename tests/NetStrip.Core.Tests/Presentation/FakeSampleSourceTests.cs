using NetStrip.Core.Presentation;

namespace NetStrip.Core.Tests.Presentation;

public class FakeSampleSourceTests
{
    [Fact]
    public void Pattern_IsNonNegative_AndHasEightSecondsOfZerosEvery30()
    {
        for (var t = 0; t < 120; t++)
        {
            var s = FakeSampleSource.PatternAt(t, "fake");
            Assert.True(s.UpBytesPerSec >= 0 && s.DownBytesPerSec >= 0);
        }
        for (var t = 22; t < 30; t++)
        {
            var s = FakeSampleSource.PatternAt(t, "fake");
            Assert.Equal(0, s.UpBytesPerSec);
            Assert.Equal(0, s.DownBytesPerSec);
        }
        Assert.True(FakeSampleSource.PatternAt(5, "fake").DownBytesPerSec > 0);
    }

    [Fact]
    public void Exposes_OneConnectedAdapter()
    {
        using var src = new FakeSampleSource();
        Assert.NotNull(src.CurrentAdapter);
        Assert.Single(src.Adapters);
    }

    [Fact]
    public void Tick_RaisesSample_UnlessPaused()
    {
        using var src = new FakeSampleSource();
        var n = 0;
        src.SampleProduced += _ => n++;
        src.Tick();
        src.Paused = true;
        src.Tick();
        Assert.Equal(1, n);
    }
}
