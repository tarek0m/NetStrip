using NetStrip.Core.Layout;

namespace NetStrip.Core.Tests.Layout;

public class FullscreenRuleTests
{
    // QUERY_USER_NOTIFICATION_STATE values
    private const int QunsBusy = 2;
    private const int QunsD3DFullscreen = 3;
    private const int QunsPresentation = 4;
    private const int QunsAcceptsNotifications = 5;

    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PixelRect NormalWindow = new(200, 150, 1200, 700);

    [Fact]
    public void ExactMonitorRect_True()
    {
        Assert.True(FullscreenRule.IsFullscreen(Monitor, Monitor, isShellOrDesktop: false, QunsAcceptsNotifications));
    }

    [Fact]
    public void LargerThanMonitor_True()
    {
        // Borderless fullscreen windows sometimes overhang the monitor by a few pixels.
        Assert.True(FullscreenRule.IsFullscreen(new PixelRect(-8, -8, 1936, 1096), Monitor, false, QunsAcceptsNotifications));
    }

    [Fact]
    public void SecondaryMonitorExactRect_True()
    {
        var secondary = new PixelRect(1920, -200, 1440, 2560);
        Assert.True(FullscreenRule.IsFullscreen(secondary, secondary, false, QunsAcceptsNotifications));
    }

    [Fact]
    public void ShellWindow_False()
    {
        // The desktop/shell window covers the monitor but is never "a fullscreen app".
        Assert.False(FullscreenRule.IsFullscreen(Monitor, Monitor, isShellOrDesktop: true, QunsAcceptsNotifications));
    }

    [Fact]
    public void QunsD3DFullscreen_True()
    {
        Assert.True(FullscreenRule.IsFullscreen(NormalWindow, Monitor, false, QunsD3DFullscreen));
    }

    [Fact]
    public void QunsBusy_True()
    {
        Assert.True(FullscreenRule.IsFullscreen(NormalWindow, Monitor, false, QunsBusy));
    }

    [Fact]
    public void QunsPresentation_True()
    {
        Assert.True(FullscreenRule.IsFullscreen(NormalWindow, Monitor, false, QunsPresentation));
    }

    [Fact]
    public void MaximizedWithTaskbarVisible_False()
    {
        // Maximized: work area (taskbar excluded) inflated by the 8 px resize border.
        var maximized = new PixelRect(-8, -8, 1936, 1040 + 16);
        Assert.False(FullscreenRule.IsFullscreen(maximized, Monitor, false, QunsAcceptsNotifications));
    }

    [Fact]
    public void NormalWindow_NoQuns_False()
    {
        Assert.False(FullscreenRule.IsFullscreen(NormalWindow, Monitor, false, QunsAcceptsNotifications));
        Assert.False(FullscreenRule.IsFullscreen(NormalWindow, Monitor, false, 0)); // query failed
    }
}
