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
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1040);   // 40 px bottom taskbar
    private static readonly PixelRect NormalWindow = new(200, 150, 1200, 700);

    // Maximized with a visible taskbar: the work area inflated by the 8 px resize border.
    private static readonly PixelRect Maximized = new(-8, -8, 1920 + 16, 1040 + 16);

    [Fact]
    public void ExactMonitorRect_True()
    {
        Assert.True(FullscreenRule.IsFullscreen(Monitor, Monitor, WorkArea, isShellOrDesktop: false, QunsAcceptsNotifications));
    }

    [Fact]
    public void LargerThanMonitor_False()
    {
        // A window spanning two side-by-side monitors covers this one but is not fullscreen on it.
        Assert.False(FullscreenRule.IsFullscreen(new PixelRect(0, 0, 3840, 1080), Monitor, WorkArea, false, QunsAcceptsNotifications));
    }

    [Fact]
    public void MaximizedAutoHide_False()
    {
        // Auto-hide taskbar: work area = monitor, so a maximized window overhangs the whole monitor by 8 px.
        var maximizedAutoHide = new PixelRect(-8, -8, 1920 + 16, 1080 + 16);
        Assert.False(FullscreenRule.IsFullscreen(maximizedAutoHide, Monitor, Monitor, false, QunsAcceptsNotifications));
    }

    [Fact]
    public void SecondaryMonitorExactRect_True()
    {
        var secondary = new PixelRect(1920, -200, 1440, 2560);
        var secondaryWork = new PixelRect(1920, -200, 1440, 2512);
        Assert.True(FullscreenRule.IsFullscreen(secondary, secondary, secondaryWork, false, QunsAcceptsNotifications));
    }

    [Fact]
    public void ShellWindow_False()
    {
        // The desktop/shell window covers the monitor but is never "a fullscreen app".
        Assert.False(FullscreenRule.IsFullscreen(Monitor, Monitor, WorkArea, isShellOrDesktop: true, QunsAcceptsNotifications));
        Assert.False(FullscreenRule.IsFullscreen(Monitor, Monitor, WorkArea, isShellOrDesktop: true, QunsBusy));
    }

    [Fact]
    public void QunsD3DFullscreen_True()
    {
        Assert.True(FullscreenRule.IsFullscreen(Maximized, Monitor, WorkArea, false, QunsD3DFullscreen));
    }

    [Fact]
    public void QunsBusy_True()
    {
        Assert.True(FullscreenRule.IsFullscreen(Maximized, Monitor, WorkArea, false, QunsBusy));
    }

    [Fact]
    public void QunsPresentation_True()
    {
        Assert.True(FullscreenRule.IsFullscreen(Maximized, Monitor, WorkArea, false, QunsPresentation));
    }

    [Fact]
    public void QunsD3D_ForegroundSmallWindow_False()
    {
        // QUNS is system-wide: a fullscreen app elsewhere must not hide the strip on the monitor of a normal foreground window.
        Assert.False(FullscreenRule.IsFullscreen(NormalWindow, Monitor, WorkArea, false, QunsD3DFullscreen));
        Assert.False(FullscreenRule.IsFullscreen(NormalWindow, Monitor, WorkArea, false, QunsBusy));
        Assert.False(FullscreenRule.IsFullscreen(NormalWindow, Monitor, WorkArea, false, QunsPresentation));
    }

    [Fact]
    public void MaximizedWithTaskbarVisible_False()
    {
        Assert.False(FullscreenRule.IsFullscreen(Maximized, Monitor, WorkArea, false, QunsAcceptsNotifications));
    }

    [Fact]
    public void NormalWindow_NoQuns_False()
    {
        Assert.False(FullscreenRule.IsFullscreen(NormalWindow, Monitor, WorkArea, false, QunsAcceptsNotifications));
        Assert.False(FullscreenRule.IsFullscreen(NormalWindow, Monitor, WorkArea, false, 0)); // query failed
    }
}
