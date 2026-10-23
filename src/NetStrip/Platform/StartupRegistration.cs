using Microsoft.Win32;
using NetStrip.Core.Settings;

namespace NetStrip.Platform;

/// <summary>
/// "Start with Windows" via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\NetStrip</c> (no admin needed). The
/// Run value is the source of truth: the installer writes it too, and the settings checkbox reads it back.
/// </summary>
public static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "NetStrip";

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
            key.SetValue(ValueName, StartupCommand.For(exePath), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>True when Windows will start this copy of NetStrip at sign-in.</summary>
    public static bool IsEnabledFor(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return StartupCommand.Starts(key?.GetValue(ValueName) as string, exePath);
    }
}
