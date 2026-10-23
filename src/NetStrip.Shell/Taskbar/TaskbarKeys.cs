using NetStrip.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace NetStrip.Shell.Taskbar;

/// <summary>A monitor's taskbar key and its bounds/work area in physical screen pixels.</summary>
internal readonly record struct MonitorFacts(string Key, PixelRect Bounds, PixelRect WorkArea);

/// <summary>
/// The single source of taskbar keys, shared by <see cref="TaskbarHost"/> and the fullscreen watcher:
/// <c>"primary"</c> for the primary monitor (where <c>Shell_TrayWnd</c> lives), otherwise the monitor's
/// device name (<c>MONITORINFOEX.szDevice</c>, e.g. <c>\\.\DISPLAY2</c>).
/// </summary>
internal static class TaskbarKeys
{
    public const string Primary = "primary";

    /// <summary>
    /// Reads the monitor's key and rects. When <paramref name="reuseKey"/> equals the device name it is returned
    /// as-is, so steady-state polling does not allocate.
    /// </summary>
    public static unsafe bool TryGetMonitor(HMONITOR monitor, string? reuseKey, out MonitorFacts facts)
    {
        facts = default;
        if (monitor.IsNull) return false;

        MONITORINFOEXW info = default;
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info)) return false;

        string key;
        if ((info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0)
        {
            key = Primary;
        }
        else
        {
            ReadOnlySpan<char> device = info.szDevice.AsSpan();
            var end = device.IndexOf('\0');
            if (end >= 0) device = device[..end];
            if (device.IsEmpty) return false;
            key = reuseKey is not null && device.SequenceEqual(reuseKey) ? reuseKey : device.ToString();
        }

        facts = new MonitorFacts(key, ToPixelRect(info.monitorInfo.rcMonitor), ToPixelRect(info.monitorInfo.rcWork));
        return true;
    }

    public static PixelRect ToPixelRect(in RECT r) => new(r.left, r.top, r.right - r.left, r.bottom - r.top);
}
