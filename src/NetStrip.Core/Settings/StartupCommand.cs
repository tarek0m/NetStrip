namespace NetStrip.Core.Settings;

/// <summary>The "Start with Windows" Run-key value: written by the app and the installer, read back to show the truth.</summary>
public static class StartupCommand
{
    /// <summary>The value to store: the exe path, quoted because it may contain spaces.</summary>
    public static string For(string exePath) => "\"" + exePath + "\"";

    /// <summary>
    /// True when <paramref name="runValue"/> starts <paramref name="exePath"/>, with or without quotes and arguments
    /// (Windows paths are case-insensitive). A value left by another copy (an old build folder) does not count.
    /// </summary>
    public static bool Starts(string? runValue, string exePath)
    {
        var value = runValue?.Trim();
        if (string.IsNullOrEmpty(value)) return false;

        string command;
        if (value[0] == '"')
        {
            var close = value.IndexOf('"', 1);
            command = close < 0 ? value[1..] : value[1..close];
        }
        else
        {
            command = value;
        }
        return string.Equals(command.Trim(), exePath, StringComparison.OrdinalIgnoreCase);
    }
}
