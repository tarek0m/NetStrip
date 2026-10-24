using NetStrip.Core;
using NetStrip.Core.Formatting;

namespace NetStrip.Platform;

/// <summary>Wording of the data cap warnings (shown as a Windows notification and as a flyout notice).</summary>
internal static class CapWarning
{
    /// <summary>The warning for <paramref name="level"/>, or null for <see cref="CapLevel.None"/>.</summary>
    public static string? Message(CapLevel level, long used, long cap)
    {
        if (level == CapLevel.None) return null;
        var u = SpeedFormatter.FormatTotal(used);
        var c = SpeedFormatter.FormatTotal(cap);
        return level == CapLevel.Reached100
            ? $"You've reached your monthly data cap ({u} of {c})"
            : $"You've used 80% of your monthly data ({u} of {c})";
    }
}
