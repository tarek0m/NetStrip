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
    /// <param name="isShellOrDesktop">The window is the shell/desktop (never counts as a fullscreen app).</param>
    /// <param name="quns">SHQueryUserNotificationState value, or 0 when the query failed.</param>
    public static bool IsFullscreen(PixelRect window, PixelRect monitor, bool isShellOrDesktop, int quns)
    {
        if (isShellOrDesktop) return false;
        if (quns is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode) return true;

        // Covers the whole monitor (borderless fullscreen windows may overhang slightly).
        return window.X <= monitor.X
            && window.Y <= monitor.Y
            && window.X + window.Width >= monitor.X + monitor.Width
            && window.Y + window.Height >= monitor.Y + monitor.Height;
    }
}
