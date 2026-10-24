using NetStrip.Core;
using NetStrip.Core.Formatting;
using NetStrip.Core.Presentation;

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
        Cap = 32,
    }

    /// <summary>E.g. "1.20 MB/s".</summary>
    public string UpText { get; private set; } = "";
    public string DownText { get; private set; } = "";

    /// <summary>Compact pieces: the down piece omits its unit when both speeds share one.</summary>
    public string CompactDown { get; private set; } = "";
    public string CompactUp { get; private set; } = "";

    public string Tooltip { get; private set; } = "";
    public string AccessibleName { get; private set; } = "";

    /// <summary>How far through the data cap this cycle is; drives the arrow tint.</summary>
    public CapLevel CapLevel { get; private set; } = CapLevel.None;

    // Inputs of the current Tooltip, so an unchanged tick (e.g. idle) does not format it again.
    private Totals _tooltipToday = new(-1, -1);
    private (long Used, long Cap)? _tooltipCap;

    /// <param name="cap">This cycle's cap progress in bytes (<c>IUsageQuery.CapProgress</c>); null when no cap is set.</param>
    public Changes Update(Sample s, Totals today, (long Used, long Cap)? cap, SpeedUnits units)
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

        if (today != _tooltipToday || cap != _tooltipCap)
        {
            _tooltipToday = today;
            _tooltipCap = cap;
            var tooltip = CapStatus.Tooltip(today, cap);
            if (tooltip != Tooltip) { Tooltip = tooltip; changes |= Changes.Tooltip; }
        }

        var level = CapStatus.Level(cap);
        if (level != CapLevel) { CapLevel = level; changes |= Changes.Cap; }

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
