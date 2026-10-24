using NetStrip.Core.Layout;

namespace NetStrip.Core.Tests.Layout;

public class TaskbarItemsTests
{
    private const int Y = 1040, H = 48;

    private static TaskbarItem Item(int x, int width, bool start = false) => new(new PixelRect(x, Y, width, H), start);

    private static readonly TaskbarItem[] LeftAlignedApps =
    [
        Item(0, 48, start: true),     // Start
        Item(48, 150),                // Search box
        Item(198, 48),                // Task View
        Item(246, 48), Item(294, 48), Item(342, 48),   // app buttons
    ];

    [Fact]
    public void LeftAligned_WidgetsAtTheRight_IsTheTrayNeighbor()
    {
        var items = LeftAlignedApps.Append(Item(1500, 150)).ToList();   // Widgets (weather) just before the tray

        var (apps, neighbor) = TaskbarItems.Split(items);

        Assert.Equal(new PixelRect(0, Y, 390, H), apps);
        Assert.Equal(new PixelRect(1500, Y, 150, H), neighbor);
    }

    [Fact]
    public void Centered_WidgetsAtTheLeft_IsIgnored()
    {
        var items = new[] { Item(0, 150), Item(700, 48, start: true), Item(748, 48), Item(796, 48), Item(844, 48) };

        var (apps, neighbor) = TaskbarItems.Split(items);

        Assert.Equal(new PixelRect(700, Y, 192, H), apps);
        Assert.Null(neighbor);
    }

    [Fact]
    public void StartNotRecognised_UsesTheLargestGroup()
    {
        var items = LeftAlignedApps.Select(i => i with { IsStart = false }).Append(Item(1500, 150)).ToList();

        var (apps, neighbor) = TaskbarItems.Split(items);

        Assert.Equal(new PixelRect(0, Y, 390, H), apps);
        Assert.Equal(new PixelRect(1500, Y, 150, H), neighbor);
    }

    [Fact]
    public void SmallGapsBetweenButtons_StayOneGroup()
    {
        // 20 px gaps are below half the 48 px item height.
        var items = new[] { Item(0, 48, start: true), Item(68, 48), Item(136, 48) };

        var (apps, neighbor) = TaskbarItems.Split(items);

        Assert.Equal(new PixelRect(0, Y, 184, H), apps);
        Assert.Null(neighbor);
    }

    [Fact]
    public void NoItems_IsUnknown()
    {
        Assert.Equal((null, null), TaskbarItems.Split([]));
    }
}
