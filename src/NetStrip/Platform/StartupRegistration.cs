using Microsoft.Win32;

namespace NetStrip.Platform;

/// <summary>"Start with Windows" via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\NetStrip</c> (no admin needed).</summary>
public static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "NetStrip";

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
            key.SetValue(ValueName, "\"" + exePath + "\"", RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string { Length: > 0 };
    }
}
