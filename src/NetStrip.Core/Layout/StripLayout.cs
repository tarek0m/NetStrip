namespace NetStrip.Core.Layout;

/// <summary>
/// One taskbar's geometry in physical screen pixels. <paramref name="Tray"/> is the notification area
/// (or the clock area on secondary taskbars), <paramref name="TaskList"/> the app-button area; either may be unknown.
/// </summary>
public sealed record TaskbarGeometry(PixelRect Taskbar, PixelRect? Tray, PixelRect? TaskList, TaskbarEdge Edge, double DpiScale);

/// <summary>Pure placement rules for the strip (spec §6).</summary>
public static class StripLayout
{
    /// <summary>
    /// Places the strip on the taskbar. Sizes are physical pixels as returned by <c>IStripSurface.Measure</c>
    /// (already DPI-scaled): <paramref name="full"/> and <paramref name="compact"/> are used on horizontal taskbars,
    /// <paramref name="vertical"/> on side taskbars. The returned bounds are relative to the taskbar's top-left corner.
    /// </summary>
    public static (PixelRect Bounds, StripMode Mode) Place(TaskbarGeometry g, (int W, int H) full, (int W, int H) compact,
        (int W, int H) vertical, int paddingPx)
    {
        var (screen, mode) = IsVertical(g.Edge)
            ? PlaceVertical(g, vertical, paddingPx)
            : PlaceHorizontal(g, full, compact, paddingPx);
        return (screen with { X = screen.X - g.Taskbar.X, Y = screen.Y - g.Taskbar.Y }, mode);
    }

    /// <summary>Which monitor edge the taskbar sits on, from its rect relative to the monitor's.</summary>
    public static TaskbarEdge EdgeOf(PixelRect taskbar, PixelRect monitor)
    {
        // Compare centres (doubled to stay in integers) so partially hidden (auto-hide) taskbars still classify.
        if (taskbar.Width >= taskbar.Height)
            return 2 * taskbar.Y + taskbar.Height < 2 * monitor.Y + monitor.Height ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        return 2 * taskbar.X + taskbar.Width < 2 * monitor.X + monitor.Width ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    /// <summary>
    /// Screen rect for the floating pill: just inside the work area next to the taskbar, right-aligned with the tray
    /// (or the work area) on horizontal taskbars, at the bottom beside vertical ones; clamped to the work area.
    /// </summary>
    public static PixelRect PlaceFloating(PixelRect workArea, PixelRect? tray, TaskbarEdge edge, (int W, int H) size, int paddingPx)
    {
        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Left:
                x = workArea.X + paddingPx;
                y = Bottom(workArea) - paddingPx - size.H;
                break;
            case TaskbarEdge.Right:
                x = Right(workArea) - paddingPx - size.W;
                y = Bottom(workArea) - paddingPx - size.H;
                break;
            default:
                x = (tray is { } t ? Right(t) : Right(workArea)) - paddingPx - size.W;
                y = edge == TaskbarEdge.Top ? workArea.Y + paddingPx : Bottom(workArea) - paddingPx - size.H;
                break;
        }

        x = Math.Max(workArea.X, Math.Min(x, Right(workArea) - size.W));
        y = Math.Max(workArea.Y, Math.Min(y, Bottom(workArea) - size.H));
        return new PixelRect(x, y, size.W, size.H);
    }

    /// <summary>True when the rects share interior area (touching edges do not count).</summary>
    public static bool Intersects(PixelRect a, PixelRect b) =>
        a.X < Right(b) && b.X < Right(a) && a.Y < Bottom(b) && b.Y < Bottom(a);

    public static bool IsVertical(TaskbarEdge edge) => edge is TaskbarEdge.Left or TaskbarEdge.Right;

    /// <summary>Smallest width, as a share of compact's natural width, the strip shrinks to on a crowded taskbar.</summary>
    public const double MinCompactScale = 0.7;

    private static (PixelRect Screen, StripMode Mode) PlaceHorizontal(TaskbarGeometry g, (int W, int H) full, (int W, int H) compact, int padding)
    {
        var right = (g.Tray is { } tray ? tray.X : Right(g.Taskbar)) - padding;
        PixelRect At((int W, int H) size) =>
            new(right - size.W, g.Taskbar.Y + (g.Taskbar.Height - size.H) / 2, size.W, size.H);

        var fullRect = At(full);
        if (g.TaskList is not { } taskList || !Intersects(fullRect, taskList)) return (fullRect, StripMode.Full);

        var compactRect = At(compact);
        if (!Intersects(compactRect, taskList)) return (compactRect, StripMode.Compact);

        // Crowded taskbar: shrink compact into the gap between the app buttons and the tray (its content scales down
        // uniformly), as long as it stays readable. With no usable gap it stays compact and overlaps; never hide.
        var gap = right - (Right(taskList) + padding);
        var minWidth = (int)Math.Ceiling(compact.W * MinCompactScale);
        return gap >= minWidth && gap < compact.W
            ? (At((gap, compact.H)), StripMode.Compact)
            : (compactRect, StripMode.Compact);
    }

    /// <summary>
    /// Centred on the side taskbar, just above the tray. Wider than the taskbar allows → scaled down uniformly (the
    /// surface's content scales with it), so it always fits.
    /// </summary>
    private static (PixelRect Screen, StripMode Mode) PlaceVertical(TaskbarGeometry g, (int W, int H) natural, int padding)
    {
        var available = Math.Max(1, g.Taskbar.Width - 2 * padding);
        var (w, h) = natural.W <= available
            ? natural
            : (available, Math.Max(1, (int)Math.Round(natural.H * (double)available / natural.W)));
        var bottom = (g.Tray is { } tray ? tray.Y : Bottom(g.Taskbar)) - padding;
        var x = g.Taskbar.X + (g.Taskbar.Width - w) / 2;
        return (new PixelRect(x, bottom - h, w, h), StripMode.Vertical);
    }

    private static int Right(PixelRect r) => r.X + r.Width;
    private static int Bottom(PixelRect r) => r.Y + r.Height;
}
