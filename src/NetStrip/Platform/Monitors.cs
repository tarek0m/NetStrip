using System.Runtime.InteropServices;
using NetStrip.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace NetStrip.Platform;

/// <summary>Monitor queries in physical pixels (the app is PerMonitorV2 DPI aware).</summary>
internal static class Monitors
{
    /// <summary>Work area of the monitor nearest to <paramref name="near"/>.</summary>
    public static PixelRect WorkAreaNear(PixelRect near)
    {
        var rect = new RECT { left = near.X, top = near.Y, right = near.X + near.Width, bottom = near.Y + near.Height };
        var monitor = PInvoke.MonitorFromRect(rect, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
            return new PixelRect(0, 0, 1920, 1040);
        var w = info.rcWork;
        return new PixelRect(w.left, w.top, w.right - w.left, w.bottom - w.top);
    }

    /// <summary>DPI scale (1.0 = 96 dpi) of the monitor nearest to <paramref name="near"/>.</summary>
    public static double DpiScaleNear(PixelRect near)
    {
        var rect = new RECT { left = near.X, top = near.Y, right = near.X + near.Width, bottom = near.Y + near.Height };
        var monitor = PInvoke.MonitorFromRect(rect, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Succeeded
            ? dpiX / 96.0
            : 1.0;
    }
}
