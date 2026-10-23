namespace NetStrip.Core.Layout;

/// <summary>Spec §9: is the foreground window a fullscreen app on its monitor?</summary>
public static class FullscreenRule
{
    // QUERY_USER_NOTIFICATION_STATE
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;

    /// <param name="window">Foreground window rect (screen pixels).</param>
    /// <param name="monitor">Full bounds of the monitor the window is on.</param>
    /// <param name="workArea">That monitor's work area (bounds minus visible taskbars).</param>
    /// <param name="isShellOrDesktop">The window is the shell/desktop (never counts as a fullscreen app).</param>
    /// <param name="quns">SHQueryUserNotificationState value, or 0 when the query failed.</param>
    public static bool IsFullscreen(PixelRect window, PixelRect monitor, PixelRect workArea, bool isShellOrDesktop, int quns)
    {
        if (isShellOrDesktop) return false;

        // Exactly the monitor's bounds: fullscreen on its own. Larger rects (maximized windows overhanging an
        // auto-hide taskbar monitor, windows spanning several monitors) are not.
        if (window == monitor) return true;

        // QUNS is system-wide, so it only counts for a foreground window that is at least work-area sized;
        // otherwise a fullscreen app on another monitor would hide this monitor's strip.
        return quns is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode && Covers(window, workArea);
    }

    private static bool Covers(PixelRect outer, PixelRect inner) =>
        outer.X <= inner.X
        && outer.Y <= inner.Y
        && outer.X + outer.Width >= inner.X + inner.Width
        && outer.Y + outer.Height >= inner.Y + inner.Height;
}
