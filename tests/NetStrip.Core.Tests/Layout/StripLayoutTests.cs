using NetStrip.Core.Layout;

namespace NetStrip.Core.Tests.Layout;

public class StripLayoutTests
{
    private const int Padding = 4;
    private static readonly (int W, int H) Full = (90, 40);
    private static readonly (int W, int H) Compact = (60, 20);
    private static readonly (int W, int H) Vertical = (44, 70);

    // 1920x1080 monitor, 48 px bottom taskbar, tray on the right.
    private static readonly PixelRect BottomTaskbar = new(0, 1040, 1920, 48);
    private static readonly PixelRect BottomTray = new(1700, 1040, 220, 48);

    [Fact]
    public void Bottom_LeftOfTray()
    {
        var g = new TaskbarGeometry(BottomTaskbar, BottomTray, new PixelRect(600, 1040, 700, 48), TaskbarEdge.Bottom, 1.0);

        var (bounds, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        // Right edge = tray left - padding = 1696 (screen) -> x = 1606; vertically centred: (48 - 40) / 2 = 4.
        Assert.Equal(StripMode.Full, mode);
        Assert.Equal(new PixelRect(1606, 4, 90, 40), bounds);
    }

    [Fact]
    public void Top_LeftOfTray_RelativeToTaskbar()
    {
        var g = new TaskbarGeometry(new PixelRect(0, 0, 1920, 48), new PixelRect(1700, 0, 220, 48), null, TaskbarEdge.Top, 1.0);

        var (bounds, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        Assert.Equal(StripMode.Full, mode);
        Assert.Equal(new PixelRect(1606, 4, 90, 40), bounds);
    }

    [Fact]
    public void Bottom_NoTray_RightAligned()
    {
        // Secondary taskbar on a monitor to the right of the primary one.
        var g = new TaskbarGeometry(new PixelRect(1920, 1040, 1920, 48), null, null, TaskbarEdge.Bottom, 1.0);

        var (bounds, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        // Right edge = taskbar right - padding = 3836 (screen) -> x = 3746 screen = 1826 relative.
        Assert.Equal(StripMode.Full, mode);
        Assert.Equal(new PixelRect(1826, 4, 90, 40), bounds);
    }

    [Fact]
    public void CenteredIcons_Overlap_GoesCompact()
    {
        // App buttons end at 1620; the full strip would span 1606..1696.
        var g = new TaskbarGeometry(BottomTaskbar, BottomTray, new PixelRect(500, 1040, 1120, 48), TaskbarEdge.Bottom, 1.0);

        var (bounds, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        // Compact positioned the same way: right edge 1696 -> x = 1636; y = (48 - 20) / 2 = 14.
        Assert.Equal(StripMode.Compact, mode);
        Assert.Equal(new PixelRect(1636, 14, 60, 20), bounds);
    }

    [Fact]
    public void CompactStillOverlaps_StaysCompact()
    {
        // App buttons end at 1680, overlapping even the compact strip (1636..1696).
        var g = new TaskbarGeometry(BottomTaskbar, BottomTray, new PixelRect(500, 1040, 1180, 48), TaskbarEdge.Bottom, 1.0);

        var (bounds, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        Assert.Equal(StripMode.Compact, mode);
        Assert.Equal(new PixelRect(1636, 14, 60, 20), bounds);
    }

    [Fact]
    public void CompactSlightlyTooWide_ShrinksIntoTheGap()
    {
        // App buttons end at 1640: gap = 1696 - (1640 + 4) = 52, between 70 % of compact (42) and its width (60).
        var g = new TaskbarGeometry(BottomTaskbar, BottomTray, new PixelRect(500, 1040, 1140, 48), TaskbarEdge.Bottom, 1.0);

        var (bounds, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        Assert.Equal(StripMode.Compact, mode);
        Assert.Equal(new PixelRect(1644, 14, 52, 20), bounds);   // still right-aligned to the tray, nothing overlaps
    }

    [Fact]
    public void CompactGap_BelowMinimumScale_DoesNotShrink()
    {
        // Gap = 1696 - (1655 + 4) = 37 < 42: shrinking further would be unreadable, so keep the natural size.
        var g = new TaskbarGeometry(BottomTaskbar, BottomTray, new PixelRect(500, 1040, 1155, 48), TaskbarEdge.Bottom, 1.0);

        var (bounds, _) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        Assert.Equal(new PixelRect(1636, 14, 60, 20), bounds);
    }

    [Fact]
    public void TaskListTouchingButNotOverlapping_StaysFull()
    {
        // App buttons end exactly where the full strip starts (1606): edges touching is not an overlap.
        var g = new TaskbarGeometry(BottomTaskbar, BottomTray, new PixelRect(500, 1040, 1106, 48), TaskbarEdge.Bottom, 1.0);

        var (_, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        Assert.Equal(StripMode.Full, mode);
    }

    [Fact]
    public void Vertical_Fits_NaturalSize_CentredAboveTray()
    {
        // 62 px left taskbar: 44 <= 62 - 2*4 = 54.
        var g = new TaskbarGeometry(new PixelRect(0, 0, 62, 1040), new PixelRect(0, 900, 62, 140), null, TaskbarEdge.Left, 1.0);

        var (bounds, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        // Directly above the tray: bottom = 900 - 4 = 896 -> y = 826; centred horizontally: (62 - 44) / 2 = 9.
        Assert.Equal(StripMode.Vertical, mode);
        Assert.Equal(new PixelRect(9, 826, 44, 70), bounds);
    }

    [Fact]
    public void Vertical_TooNarrow_ScalesDownUniformly()
    {
        // 48 px right taskbar at x = 1872: available 48 - 8 = 40 < 44, so 40 x round(70 * 40 / 44) = 40 x 64.
        var g = new TaskbarGeometry(new PixelRect(1872, 0, 48, 1080), new PixelRect(1872, 900, 48, 180), null, TaskbarEdge.Right, 1.0);

        var (bounds, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        Assert.Equal(StripMode.Vertical, mode);
        Assert.Equal(new PixelRect(4, 832, 40, 64), bounds);
    }

    [Fact]
    public void Vertical_NeverUsesFullOrCompact_EvenWhenWideEnough()
    {
        // Upload above download on side taskbars, however wide they are.
        var g = new TaskbarGeometry(new PixelRect(0, 0, 200, 1080), new PixelRect(0, 900, 200, 180), null, TaskbarEdge.Left, 1.0);

        var (_, mode) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        Assert.Equal(StripMode.Vertical, mode);
    }

    [Fact]
    public void Vertical_NoTray_AboveTaskbarBottom()
    {
        var g = new TaskbarGeometry(new PixelRect(0, 0, 100, 1080), null, null, TaskbarEdge.Left, 1.0);

        var (bounds, _) = StripLayout.Place(g, Full, Compact, Vertical, Padding);

        Assert.Equal(new PixelRect(28, 1006, 44, 70), bounds);
    }

    [Fact]
    public void Dpi150_UsesProvidedSizes()
    {
        // At 150 % the surface already measured in physical pixels; layout must not scale again.
        var g = new TaskbarGeometry(new PixelRect(0, 1032, 1920, 72), new PixelRect(1600, 1032, 320, 72), null, TaskbarEdge.Bottom, 1.5);

        var (bounds, mode) = StripLayout.Place(g, (135, 60), (90, 30), Vertical, paddingPx: 6);

        Assert.Equal(StripMode.Full, mode);
        Assert.Equal(new PixelRect(1600 - 6 - 135, 6, 135, 60), bounds);
    }

    [Theory]
    [InlineData(0, 1040, 1920, 40, TaskbarEdge.Bottom)]
    [InlineData(0, 0, 1920, 40, TaskbarEdge.Top)]
    [InlineData(0, 0, 62, 1080, TaskbarEdge.Left)]
    [InlineData(1858, 0, 62, 1080, TaskbarEdge.Right)]
    public void EdgeOf_FromRectRelativeToMonitor(int x, int y, int w, int h, TaskbarEdge expected)
    {
        Assert.Equal(expected, StripLayout.EdgeOf(new PixelRect(x, y, w, h), new PixelRect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void EdgeOf_SecondaryMonitorWithNegativeOrigin()
    {
        var monitor = new PixelRect(-1440, -200, 1440, 2560);

        Assert.Equal(TaskbarEdge.Bottom, StripLayout.EdgeOf(new PixelRect(-1440, 2312, 1440, 48), monitor));
        Assert.Equal(TaskbarEdge.Top, StripLayout.EdgeOf(new PixelRect(-1440, -200, 1440, 48), monitor));
    }

    [Fact]
    public void Floating_BottomTaskbar_JustAboveTray()
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);

        var rect = StripLayout.PlaceFloating(workArea, BottomTray, TaskbarEdge.Bottom, (100, 44), Padding);

        // Right-aligned with the tray's right edge (1920 - 4), sitting just above the work-area bottom.
        Assert.Equal(new PixelRect(1816, 1040 - 4 - 44, 100, 44), rect);
    }

    [Fact]
    public void Floating_TopTaskbar_BelowIt()
    {
        var workArea = new PixelRect(0, 48, 1920, 1032);

        var rect = StripLayout.PlaceFloating(workArea, new PixelRect(1700, 0, 220, 48), TaskbarEdge.Top, (100, 44), Padding);

        Assert.Equal(new PixelRect(1816, 52, 100, 44), rect);
    }

    [Fact]
    public void Floating_NoTaskbar_BottomRightOfWorkArea()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);

        var rect = StripLayout.PlaceFloating(workArea, null, TaskbarEdge.Bottom, (100, 44), Padding);

        Assert.Equal(new PixelRect(1816, 1032, 100, 44), rect);
    }

    [Fact]
    public void Floating_LeftTaskbar_BesideItAtTheBottom()
    {
        var workArea = new PixelRect(62, 0, 1858, 1080);

        var rect = StripLayout.PlaceFloating(workArea, new PixelRect(0, 900, 62, 180), TaskbarEdge.Left, (100, 44), Padding);

        Assert.Equal(new PixelRect(66, 1032, 100, 44), rect);
    }

    [Fact]
    public void Floating_ClampedInsideWorkArea()
    {
        // A tray far to the left must not push the pill outside the work area.
        var workArea = new PixelRect(0, 0, 1920, 1040);

        var rect = StripLayout.PlaceFloating(workArea, new PixelRect(0, 1040, 50, 48), TaskbarEdge.Bottom, (100, 44), Padding);

        Assert.Equal(0, rect.X);
    }
}
