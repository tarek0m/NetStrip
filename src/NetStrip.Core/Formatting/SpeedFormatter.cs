using System.Globalization;

namespace NetStrip.Core.Formatting;

/// <summary>
/// Compact speed/total text. Digits: &lt;10 -> 2 decimals, &lt;100 -> 1 decimal, otherwise 0 decimals;
/// a value that would display as 1000 or more moves up one unit. Uses F-formats only (no group separators).
/// </summary>
public static class SpeedFormatter
{
    private static readonly string[] ByteSpeedUnits = ["KB/s", "MB/s", "GB/s"];
    private static readonly string[] BitSpeedUnits = ["Kb/s", "Mb/s", "Gb/s"];
    private static readonly string[] ByteSpeedWords = ["kilobytes", "megabytes", "gigabytes"];
    private static readonly string[] BitSpeedWords = ["kilobits", "megabits", "gigabits"];
    private static readonly string[] TotalUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatSpeed(long bytesPerSec, SpeedUnits units, CultureInfo? culture = null)
    {
        var (value, index) = Scale(bytesPerSec, units, ByteSpeedUnits.Length);
        var unit = units == SpeedUnits.Bytes ? ByteSpeedUnits[index] : BitSpeedUnits[index];
        return Number(value, index == 0 && value < 1, culture) + " " + unit;
    }

    public static string FormatTotal(long bytes, CultureInfo? culture = null)
    {
        var value = Math.Max(bytes, 0L) * 1.0;
        var index = 0;
        while (index < TotalUnits.Length - 1 && DisplaysAsThousand(value))
        {
            value /= 1024;
            index++;
        }
        return Number(value, false, culture, wholeOnly: index == 0) + " " + TotalUnits[index];
    }

    public static string AccessibleSpeed(long bytesPerSec, SpeedUnits units)
    {
        var (value, index) = Scale(bytesPerSec, units, ByteSpeedWords.Length);
        var word = units == SpeedUnits.Bytes ? ByteSpeedWords[index] : BitSpeedWords[index];
        return Number(value, index == 0 && value < 1, null) + " " + word + " per second";
    }

    // Converts to the first speed unit (K), then rolls up while the value would display as 1000+.
    private static (double Value, int Index) Scale(long bytesPerSec, SpeedUnits units, int unitCount)
    {
        var bytes = Math.Max(bytesPerSec, 0L) * 1.0;
        var bits = units == SpeedUnits.Bits;
        var scale = bits ? 1000.0 : 1024.0;
        var value = (bits ? bytes * 8 : bytes) / scale;
        var index = 0;
        while (index < unitCount - 1 && DisplaysAsThousand(value))
        {
            value /= scale;
            index++;
        }
        return (value, index);
    }

    private static bool DisplaysAsThousand(double value) =>
        Math.Round(value, 0, MidpointRounding.AwayFromZero) >= 1000;

    private static string Number(double value, bool belowOneUnit, CultureInfo? culture, bool wholeOnly = false)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (belowOneUnit) return "0";
        string format;
        if (wholeOnly) format = "F0";
        else if (Math.Round(value, 2, MidpointRounding.AwayFromZero) < 10) format = "F2";
        else if (Math.Round(value, 1, MidpointRounding.AwayFromZero) < 100) format = "F1";
        else format = "F0";
        return value.ToString(format, culture);
    }
}
