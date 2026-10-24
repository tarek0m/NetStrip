using System.Globalization;
using NetStrip.Core.Formatting;

namespace NetStrip.Core.Presentation;

/// <summary>
/// Data-cap state as the strip shows it: the arrow tint level and the hover tooltip. Thresholds match the cap
/// notifications and the flyout's cap bar (warning at 80 %, critical at 100 %, both inclusive). Integer arithmetic,
/// so a cycle at exactly 80 % or 100 % of the cap is never misread by floating-point rounding.
/// </summary>
public static class CapStatus
{
    /// <summary><see cref="CapLevel.None"/> when there is no cap (or a cap of 0), else the highest threshold reached.</summary>
    public static CapLevel Level(long used, long cap)
    {
        if (cap <= 0) return CapLevel.None;
        var u = (Int128)Math.Max(used, 0L);
        if (u >= cap) return CapLevel.Reached100;
        return u * 5 >= (Int128)cap * 4 ? CapLevel.Warning80 : CapLevel.None;
    }

    /// <inheritdoc cref="Level(long, long)" />
    public static CapLevel Level((long Used, long Cap)? progress) =>
        progress is { } p ? Level(p.Used, p.Cap) : CapLevel.None;

    /// <summary>
    /// Whole percent of the cap used, rounded down so it never reads 80 % or 100 % before that threshold (and its
    /// tint) is actually reached. Not capped at 100; 0 when <paramref name="cap"/> is 0 or less.
    /// </summary>
    public static int Percent(long used, long cap)
    {
        if (cap <= 0) return 0;
        var percent = (Int128)Math.Max(used, 0L) * 100 / cap;
        return percent > int.MaxValue ? int.MaxValue : (int)percent;
    }

    /// <summary>
    /// The strip's tooltip: "Today ↓ 1.24 GB ↑ 210 MB", plus "This cycle: 62% of 50.0 GB" on a second line when a
    /// cap is set.
    /// </summary>
    public static string Tooltip(Totals today, (long Used, long Cap)? cap, CultureInfo? culture = null)
    {
        var line = $"Today ↓ {SpeedFormatter.FormatTotal(today.DownBytes, culture)} ↑ {SpeedFormatter.FormatTotal(today.UpBytes, culture)}";
        if (cap is not { Cap: > 0 } c) return line;
        return line + "\nThis cycle: " + Percent(c.Used, c.Cap).ToString(culture ?? CultureInfo.CurrentCulture) +
            "% of " + SpeedFormatter.FormatTotal(c.Cap, culture);
    }
}
