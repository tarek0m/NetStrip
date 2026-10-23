using NetStrip.Core;
using NetStrip.Core.Formatting;

namespace NetStrip.Strip;

/// <summary>
/// Formats everything the strip displays and reports which pieces changed, so the view touches
/// the visual tree only when text actually differs (§12).
/// </summary>
internal sealed class StripViewModel
{
    [Flags]
    public enum Changes
    {
        None = 0,
        Up = 1,
        Down = 2,
        Compact = 4,
        Tooltip = 8,
        Name = 16,
    }

    /// <summary>E.g. "1.20 MB/s".</summary>
    public string UpText { get; private set; } = "";
    public string DownText { get; private set; } = "";

    /// <summary>Compact pieces: the down piece omits its unit when both speeds share one.</summary>
    public string CompactDown { get; private set; } = "";
    public string CompactUp { get; private set; } = "";

    public string Tooltip { get; private set; } = "";
    public string AccessibleName { get; private set; } = "";

    public Changes Update(Sample s, Totals today, SpeedUnits units)
    {
        var up = SpeedFormatter.FormatSpeed(s.UpBytesPerSec, units);
        var down = SpeedFormatter.FormatSpeed(s.DownBytesPerSec, units);
        var changes = Changes.None;

        if (up != UpText) { UpText = up; changes |= Changes.Up; }
        if (down != DownText) { DownText = down; changes |= Changes.Down; }

        if ((changes & (Changes.Up | Changes.Down)) != 0)
        {
            // "↓8.40 ↑1.20 MB/s" when units match; otherwise each speed carries its own unit.
            var (downNumber, downUnit) = Split(down);
            var compactDown = downUnit == Split(up).Unit ? downNumber : down;
            if (compactDown != CompactDown || up != CompactUp)
            {
                CompactDown = compactDown;
                CompactUp = up;
                changes |= Changes.Compact;
            }
        }

        var tooltip = $"Today ↓ {SpeedFormatter.FormatTotal(today.DownBytes)} ↑ {SpeedFormatter.FormatTotal(today.UpBytes)}";
        if (tooltip != Tooltip) { Tooltip = tooltip; changes |= Changes.Tooltip; }

        if ((changes & (Changes.Up | Changes.Down)) != 0)
        {
            AccessibleName =
                $"Upload {SpeedFormatter.AccessibleSpeed(s.UpBytesPerSec, units)}, download {SpeedFormatter.AccessibleSpeed(s.DownBytesPerSec, units)}";
            changes |= Changes.Name;
        }
        return changes;
    }

    /// <summary>"1.20 MB/s" → ("1.20", "MB/s").</summary>
    internal static (string Number, string Unit) Split(string speed)
    {
        var i = speed.LastIndexOf(' ');
        return (speed[..i], speed[(i + 1)..]);
    }
}
