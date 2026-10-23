using NetStrip.Core;
using NetStrip.Core.Layout;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace NetStrip.Shell.Taskbar;

/// <summary>A taskbar window and its key (see <see cref="TaskbarKeys"/>).</summary>
internal readonly record struct TaskbarHandle(string Key, HWND Hwnd);

/// <summary>A located taskbar: key, handle, geometry (TaskList not filled; see <see cref="TaskListProbe"/>) and its monitor's work area.</summary>
internal sealed record LocatedTaskbar(string Key, HWND Hwnd, TaskbarGeometry Geometry, PixelRect WorkArea);

/// <summary>Finds Explorer's taskbars and reads their geometry (spec §6 Attach). All results are physical screen pixels.</summary>
internal static class TaskbarLocator
{
    private const string PrimaryClass = "Shell_TrayWnd";
    private const string SecondaryClass = "Shell_SecondaryTrayWnd";
    private const string TrayClass = "TrayNotifyWnd";
    private const string SecondaryClockClass = "ClockButton";   // Windows 10 secondary taskbars with a clock

    /// <summary>Cheap handle lookup: the primary taskbar (if present) first, then visible secondary taskbars when <paramref name="scope"/> is All.</summary>
    public static List<TaskbarHandle> Find(MonitorScope scope)
    {
        var result = new List<TaskbarHandle>(2);
        var primary = PInvoke.FindWindow(PrimaryClass, null);
        if (!primary.IsNull) result.Add(new TaskbarHandle(TaskbarKeys.Primary, primary));
        if (scope != MonitorScope.All) return result;

        var hwnd = HWND.Null;
        while (!(hwnd = PInvoke.FindWindowEx(HWND.Null, hwnd, SecondaryClass, null)).IsNull)
        {
            if (!PInvoke.IsWindowVisible(hwnd)) continue;
            var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            if (!TaskbarKeys.TryGetMonitor(monitor, null, out var facts)) continue;
            if (facts.Key == TaskbarKeys.Primary || result.Exists(t => t.Key == facts.Key)) continue;
            result.Add(new TaskbarHandle(facts.Key, hwnd));
        }
        return result;
    }

    /// <summary>Reads the taskbar's rect, edge, tray (or secondary clock) rect and DPI scale; null if the window is gone.</summary>
    public static LocatedTaskbar? TryLocate(TaskbarHandle taskbar)
    {
        if (!PInvoke.IsWindow(taskbar.Hwnd) || !PInvoke.GetWindowRect(taskbar.Hwnd, out var rect)) return null;
        var monitor = PInvoke.MonitorFromWindow(taskbar.Hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (!TaskbarKeys.TryGetMonitor(monitor, taskbar.Key, out var facts)) return null;

        var bounds = TaskbarKeys.ToPixelRect(rect);
        var edge = StripLayout.EdgeOf(bounds, facts.Bounds);
        var tray = VisibleChildRect(taskbar.Hwnd, taskbar.Key == TaskbarKeys.Primary ? TrayClass : SecondaryClockClass);
        var dpi = PInvoke.GetDpiForWindow(taskbar.Hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;

        return new LocatedTaskbar(taskbar.Key, taskbar.Hwnd, new TaskbarGeometry(bounds, tray, null, edge, scale), facts.WorkArea);
    }

    private static PixelRect? VisibleChildRect(HWND parent, string className)
    {
        var child = PInvoke.FindWindowEx(parent, HWND.Null, className, null);
        if (child.IsNull || !PInvoke.IsWindowVisible(child) || !PInvoke.GetWindowRect(child, out var rect)) return null;
        var r = TaskbarKeys.ToPixelRect(rect);
        return r.Width > 0 && r.Height > 0 ? r : null;
    }
}
