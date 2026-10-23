using NetStrip.Core;
using NetStrip.Core.Layout;
using NetStrip.Shell.Taskbar;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace NetStrip.Shell.Fullscreen;

/// <summary>
/// Spec §9: reports which monitor (by taskbar key, matching <see cref="TaskbarHost.Surfaces"/>) has a fullscreen
/// foreground app. <see cref="Poll"/> is called once per sampler tick on the UI thread; it raises
/// <see cref="FullscreenChanged"/> only on changes and does not allocate in the steady state. Never throws.
/// </summary>
public sealed class FullscreenWatcher(ILog log)
{
    private string? _fullscreenKey;     // monitor currently reported fullscreen, if any
    private string? _lastMonitorKey;    // reused to avoid allocating the device name every poll
    private bool _errorLogged;

    /// <summary>(taskbarKey, isFullscreen) — raised only when a monitor's state changes.</summary>
    public event Action<string, bool>? FullscreenChanged;

    public void Poll()
    {
        string? key;
        try
        {
            key = DetectFullscreenMonitor();
            _errorLogged = false;
        }
        catch (Exception ex)
        {
            if (!_errorLogged) log.Warn("FullscreenWatcher: detection failed", ex);
            _errorLogged = true;
            return;
        }

        if (string.Equals(key, _fullscreenKey, StringComparison.Ordinal)) return;
        var previous = _fullscreenKey;
        _fullscreenKey = key;
        if (previous is not null) Raise(previous, false);
        if (key is not null) Raise(key, true);
    }

    private string? DetectFullscreenMonitor()
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull) return null;

        var monitor = PInvoke.MonitorFromWindow(foreground, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
        if (!TaskbarKeys.TryGetMonitor(monitor, _lastMonitorKey, out var facts)) return null;
        _lastMonitorKey = facts.Key;

        if (!PInvoke.GetWindowRect(foreground, out var rect)) return null;
        var quns = PInvoke.SHQueryUserNotificationState(out var state).Succeeded ? (int)state : 0;

        return FullscreenRule.IsFullscreen(TaskbarKeys.ToPixelRect(rect), facts.Bounds, IsShellOrDesktop(foreground), quns)
            ? facts.Key
            : null;
    }

    private static bool IsShellOrDesktop(HWND hwnd)
    {
        if (hwnd == PInvoke.GetShellWindow() || hwnd == PInvoke.GetDesktopWindow()) return true;

        // Clicking the desktop focuses Progman or a WorkerW; clicking a taskbar focuses the taskbar itself.
        Span<char> buffer = stackalloc char[64];
        var length = PInvoke.GetClassName(hwnd, buffer);
        if (length <= 0) return false;
        ReadOnlySpan<char> className = buffer[..length];
        return className.SequenceEqual("Progman")
            || className.SequenceEqual("WorkerW")
            || className.SequenceEqual("Shell_TrayWnd")
            || className.SequenceEqual("Shell_SecondaryTrayWnd");
    }

    private void Raise(string key, bool fullscreen)
    {
        try
        {
            FullscreenChanged?.Invoke(key, fullscreen);
        }
        catch (Exception ex)
        {
            log.Error($"FullscreenWatcher: FullscreenChanged handler threw for '{key}'", ex);
        }
    }
}
