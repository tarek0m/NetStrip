using NetStrip.Core.Presentation;

namespace NetStrip.Core.Tests.Presentation;

public class GraphBufferTests
{
    private static Sample S(long up, long down, int sec = 0) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(sec), "a", up, down);

    [Fact]
    public void Graph_KeepsLast60()
    {
        var g = new GraphBuffer();
        for (var i = 0; i < 100; i++) g.Push(S(i, i, i));
        Assert.Equal(60, g.Items.Count);
        Assert.Equal(40, g.Items[0].UpBytesPerSec);
        Assert.Equal(99, g.Items[59].UpBytesPerSec);
    }

    [Fact]
    public void Graph_MaxFloor1024()
    {
        var g = new GraphBuffer();
        Assert.Equal(1024, g.Max);
        g.Push(S(10, 20));
        Assert.Equal(1024, g.Max);
        g.Push(S(5000, 20));
        Assert.Equal(5000, g.Max);
        g.Push(S(10, 9000));
        Assert.Equal(9000, g.Max);
    }

    [Fact]
    public void Graph_MaxDropsWhenPeakLeavesWindow()
    {
        var g = new GraphBuffer(3);
        g.Push(S(9000, 0));
        g.Push(S(1, 1)); g.Push(S(1, 1)); g.Push(S(1, 1));
        Assert.Equal(1024, g.Max);
    }
}
