using NetStrip.Core.Presentation;

namespace NetStrip.Core.Tests.Presentation;

public class FlyoutPlacementTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);   // 40 px taskbar at the bottom
    private const int W = 320, H = 400, Gap = 8, Margin = 8;

    [Fact]
    public void Bottom_CentersAboveStrip_WithGap()
    {
        var strip = new PixelRect(1500, 1040, 100, 40);
        var p = FlyoutPlacement.Compute(strip, TaskbarEdge.Bottom, W, H, Work, Gap, Margin);
        Assert.Equal(1550 - 160, p.X);
        Assert.Equal(1040 - H - Gap, p.Y);
    }

    [Fact]
    public void Bottom_ClampsToRightEdgeOfWorkArea()
    {
        var strip = new PixelRect(1880, 1040, 100, 40);
        var p = FlyoutPlacement.Compute(strip, TaskbarEdge.Bottom, W, H, Work, Gap, Margin);
        Assert.Equal(1920 - W - Margin, p.X);
    }

    [Fact]
    public void Bottom_ClampsToLeftEdgeOfWorkArea()
    {
        var strip = new PixelRect(0, 1040, 100, 40);
        var p = FlyoutPlacement.Compute(strip, TaskbarEdge.Bottom, W, H, Work, Gap, Margin);
        Assert.Equal(Margin, p.X);
    }

    [Fact]
    public void Top_PlacesBelowStrip()
    {
        var work = new PixelRect(0, 40, 1920, 1040);
        var strip = new PixelRect(900, 0, 100, 40);
        var p = FlyoutPlacement.Compute(strip, TaskbarEdge.Top, W, H, work, Gap, Margin);
        Assert.Equal(40 + Gap, p.Y);
    }

    [Fact]
    public void Left_PlacesBesideStrip_ClampedVertically()
    {
        var work = new PixelRect(60, 0, 1860, 1080);
        var strip = new PixelRect(0, 1000, 60, 80);
        var p = FlyoutPlacement.Compute(strip, TaskbarEdge.Left, W, H, work, Gap, Margin);
        Assert.Equal(60 + Gap, p.X);
        Assert.Equal(1080 - H - Margin, p.Y);
    }

    [Fact]
    public void Right_PlacesBesideStrip()
    {
        var work = new PixelRect(0, 0, 1860, 1080);
        var strip = new PixelRect(1860, 500, 60, 80);
        var p = FlyoutPlacement.Compute(strip, TaskbarEdge.Right, W, H, work, Gap, Margin);
        Assert.Equal(1860 - W - Gap, p.X);
        Assert.Equal(540 - H / 2, p.Y);
    }

    [Fact]
    public void WindowLargerThanWorkArea_PinsToTopLeftMargin()
    {
        var p = FlyoutPlacement.Compute(new PixelRect(0, 100, 50, 40), TaskbarEdge.Bottom, 5000, 5000,
            new PixelRect(0, 0, 800, 600), Gap, Margin);
        Assert.Equal(new PixelPoint(Margin, Margin), p);
    }
}
