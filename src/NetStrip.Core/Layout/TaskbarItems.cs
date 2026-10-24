namespace NetStrip.Core.Layout;

/// <summary>One top-level item of a Windows 11 taskbar (Start, Search, Task View, Widgets, an app button), in screen pixels.</summary>
public readonly record struct TaskbarItem(PixelRect Bounds, bool IsStart);

/// <summary>
/// Splits the Windows 11 taskbar's items into the app-button group and whatever sits to its right (spec §6). With the
/// taskbar aligned left, Widgets (the weather) moves to the far right, just before the tray: it is not an app button,
/// but the strip can't go over it either, so it becomes the strip's right boundary instead of the tray.
/// </summary>
public static class TaskbarItems
{
    /// <summary>
    /// Items are grouped where the gap between neighbours exceeds half the items' typical height. The main group is the
    /// one holding Start (or, if Start isn't recognised, the one with the most items). <c>Apps</c> is that group's
    /// bounds; <c>TrayNeighbor</c> the bounds of everything to its right (null when nothing is). Items to its left, like
    /// Widgets on a centred taskbar, are ignored: the strip sits at the right end.
    /// </summary>
    public static (PixelRect? Apps, PixelRect? TrayNeighbor) Split(IReadOnlyList<TaskbarItem> items)
    {
        var sorted = items.Where(i => i.Bounds.Width > 0 && i.Bounds.Height > 0).OrderBy(i => i.Bounds.X).ToList();
        if (sorted.Count == 0) return (null, null);

        var heights = sorted.Select(i => i.Bounds.Height).Order().ToList();
        var maxGap = heights[heights.Count / 2] / 2;

        var groups = new List<List<TaskbarItem>> { new() { sorted[0] } };
        for (var i = 1; i < sorted.Count; i++)
        {
            var group = groups[^1];
            var reach = group.Max(g => g.Bounds.X + g.Bounds.Width);
            if (sorted[i].Bounds.X - reach > maxGap) groups.Add(group = new List<TaskbarItem>());
            group.Add(sorted[i]);
        }

        var main = groups.FindIndex(g => g.Exists(i => i.IsStart));
        if (main < 0) main = groups.IndexOf(groups.MaxBy(g => g.Count)!);

        var apps = Union(groups[main]);
        var right = groups.Skip(main + 1).SelectMany(g => g).ToList();
        return (apps, right.Count > 0 ? Union(right) : null);
    }

    private static PixelRect Union(IEnumerable<TaskbarItem> items)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var b in items.Select(i => i.Bounds))
        {
            left = Math.Min(left, b.X);
            top = Math.Min(top, b.Y);
            right = Math.Max(right, b.X + b.Width);
            bottom = Math.Max(bottom, b.Y + b.Height);
        }
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
