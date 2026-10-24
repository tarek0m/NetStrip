using System.Globalization;

namespace NetStrip.Core;

/// <summary>
/// The <c>--exit-after &lt;seconds&gt;</c> launch option: the app shuts down by itself, through the normal exit path,
/// once that many seconds have passed. CI uses it to launch the real app and check that it starts and exits cleanly.
/// </summary>
public static class ExitAfterOption
{
    public const string Name = "--exit-after";

    /// <summary>Longest accepted delay; anything larger is treated as a mistake and ignored.</summary>
    public static readonly TimeSpan Max = TimeSpan.FromDays(1);

    /// <summary>
    /// The delay given by <c>--exit-after &lt;seconds&gt;</c> (option name case-insensitive; whole or fractional seconds,
    /// invariant culture), or null when the option is absent or unusable. When it appears more than once, the last one
    /// decides.
    /// </summary>
    /// <param name="ignored">
    /// True when the option is present but its value is missing, not a number, not positive, or over <see cref="Max"/>.
    /// </param>
    public static TimeSpan? Parse(IReadOnlyList<string> args, out bool ignored)
    {
        TimeSpan? delay = null;
        ignored = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], Name, StringComparison.OrdinalIgnoreCase)) continue;

            delay = i + 1 < args.Count ? ToDelay(args[i + 1]) : null;
            ignored = delay is null;
        }
        return delay;
    }

    private static TimeSpan? ToDelay(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return null;
        // NaN fails both comparisons and infinity fails the second, so only finite positive values get through.
        if (!(seconds > 0) || !(seconds <= Max.TotalSeconds)) return null;
        return TimeSpan.FromSeconds(seconds);
    }
}
