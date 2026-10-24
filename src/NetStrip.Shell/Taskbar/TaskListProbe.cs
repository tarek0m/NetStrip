using System.Windows.Automation;
using NetStrip.Core;
using NetStrip.Core.Layout;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace NetStrip.Shell.Taskbar;

/// <summary>
/// What the probe found on one taskbar (physical screen pixels); null members mean "unknown" or "none". TrayNeighbor is
/// taskbar content to the right of the app buttons (Widgets on a left-aligned Win11 taskbar).
/// </summary>
internal readonly record struct ProbeResult(PixelRect? TaskList, PixelRect? ClockArea, PixelRect? TrayNeighbor = null);

/// <summary>
/// Finds the app-button area of a taskbar (spec §6 Centered-icon overlap) and, for secondary taskbars,
/// the clock area. Slow (cross-process UI Automation): call it off the UI thread. Never throws.
/// </summary>
internal static class TaskListProbe
{
    private const string XamlBridgeClass = "Windows.UI.Composition.DesktopWindowContentBridge";
    private const string TaskbarFrameId = "TaskbarFrame";
    private const string StartButtonId = "StartButton";
    private const string SystemTrayIconId = "SystemTrayIcon";
    private const string LegacyTaskListClass = "MSTaskListWClass";

    public static ProbeResult Probe(nint taskbarHwnd, bool wantClockArea, out Exception? error)
    {
        error = null;
        try
        {
            var taskbar = (HWND)taskbarHwnd;
            if (!PInvoke.IsWindow(taskbar)) return default;

            // Windows 11: XAML taskbar. Search only under the XAML bridge window so UIA never walks into our
            // own strip window (a sibling under the same taskbar), which would call back into our UI thread.
            var bridge = PInvoke.FindWindowEx(taskbar, HWND.Null, XamlBridgeClass, null);
            if (!bridge.IsNull)
            {
                var xaml = ProbeXaml(bridge, wantClockArea);
                if (xaml.TaskList is not null || xaml.ClockArea is not null) return xaml;
            }

            // Windows 10: the classic task list window.
            return new ProbeResult(LegacyTaskList(taskbar), null);
        }
        catch (Exception ex)
        {
            error = ex;
            return default;
        }
    }

    private static ProbeResult ProbeXaml(HWND bridge, bool wantClockArea)
    {
        var root = AutomationElement.FromHandle((IntPtr)bridge);
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.AutomationIdProperty);

        PixelRect? taskList = null;
        PixelRect? clock = null;
        PixelRect? trayNeighbor = null;

        // The TaskbarFrame element itself spans the whole taskbar width (observed on Win11 build 26300), so the
        // app-button area comes from its children: Start, Search, Task View, Widgets and the app buttons. Widgets is
        // split off when it sits right of the app buttons (left-aligned taskbar), see TaskbarItems.
        var frame = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, TaskbarFrameId));
        using (cache.Activate())
        {
            if (frame is not null)
                (taskList, trayNeighbor) = TaskbarItems.Split(CachedItems(frame.FindAll(TreeScope.Children, Condition.TrueCondition)));

            // Secondary taskbars on Windows 11 draw the clock in XAML (elements with AutomationId "SystemTrayIcon").
            if (wantClockArea)
                clock = UnionOfCachedBounds(root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, SystemTrayIconId)));
        }
        return new ProbeResult(taskList, clock, trayNeighbor);
    }

    private static List<TaskbarItem> CachedItems(AutomationElementCollection elements)
    {
        var items = new List<TaskbarItem>(elements.Count);
        foreach (AutomationElement element in elements)
        {
            var r = element.Cached.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
            var left = (int)Math.Floor(r.Left);
            var top = (int)Math.Floor(r.Top);
            items.Add(new TaskbarItem(
                new PixelRect(left, top, (int)Math.Ceiling(r.Right) - left, (int)Math.Ceiling(r.Bottom) - top),
                element.Cached.AutomationId == StartButtonId));
        }
        return items;
    }

    private static PixelRect? UnionOfCachedBounds(AutomationElementCollection elements)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (AutomationElement element in elements)
        {
            var r = element.Cached.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
            left = Math.Min(left, (int)Math.Floor(r.Left));
            top = Math.Min(top, (int)Math.Floor(r.Top));
            right = Math.Max(right, (int)Math.Ceiling(r.Right));
            bottom = Math.Max(bottom, (int)Math.Ceiling(r.Bottom));
        }
        return left < right && top < bottom ? new PixelRect(left, top, right - left, bottom - top) : null;
    }

    private static PixelRect? LegacyTaskList(HWND taskbar)
    {
        // Primary: ReBarWindow32 > MSTaskSwWClass > MSTaskListWClass; secondary: WorkerW > MSTaskListWClass.
        var list = FindDescendant(taskbar, LegacyTaskListClass, depth: 3);
        if (list.IsNull || !PInvoke.IsWindowVisible(list) || !PInvoke.GetWindowRect(list, out var rect)) return null;
        var r = TaskbarKeys.ToPixelRect(rect);
        return r.Width > 0 && r.Height > 0 ? r : null;
    }

    private static HWND FindDescendant(HWND parent, string className, int depth)
    {
        var direct = PInvoke.FindWindowEx(parent, HWND.Null, className, null);
        if (!direct.IsNull || depth <= 1) return direct;

        var child = HWND.Null;
        while (!(child = PInvoke.FindWindowEx(parent, child, null, null)).IsNull)
        {
            var found = FindDescendant(child, className, depth - 1);
            if (!found.IsNull) return found;
        }
        return HWND.Null;
    }
}
