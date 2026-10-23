using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetStrip.Theme;

/// <summary>
/// Brushes for the taskbar strip. The taskbar follows the *system* theme (SystemUsesLightTheme), not the app theme.
/// Brushes are frozen and replaced wholesale on <see cref="Changed"/>; consumers re-read the properties.
/// </summary>
public sealed class ThemeColors : IDisposable
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Color UploadLight = Color.FromRgb(0x2B, 0xB5, 0xA8);
    private static readonly Color UploadDark = Color.FromRgb(0x4F, 0xD1, 0xC5);
    private static readonly Color ForegroundLight = Color.FromRgb(0x1B, 0x1B, 0x1B);
    private static readonly Color ForegroundDark = Colors.White;

    private HwndSource? _source;

    public ThemeColors()
    {
        Refresh();
    }

    public bool SystemUsesLightTheme { get; private set; }
    public Brush UploadBrush { get; private set; } = null!;
    public Brush DownloadBrush { get; private set; } = null!;
    public Brush TaskbarForeground { get; private set; } = null!;

    public event Action? Changed;

    /// <summary>Starts listening for WM_SETTINGCHANGE "ImmersiveColorSet". Must be called on the UI thread.</summary>
    public void StartListening()
    {
        if (_source is not null) return;
        // Setting-change broadcasts reach top-level windows only (not message-only windows), so use a hidden popup.
        var p = new HwndSourceParameters("NetStripThemeListener") { Width = 0, Height = 0, WindowStyle = WS_POPUP };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Refresh();
            Changed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Refresh()
    {
        SystemUsesLightTheme = ReadSystemUsesLightTheme();
        UploadBrush = Frozen(SystemUsesLightTheme ? UploadLight : UploadDark);
        DownloadBrush = Frozen(SystemParameters.WindowGlassColor);
        TaskbarForeground = Frozen(SystemUsesLightTheme ? ForegroundLight : ForegroundDark);
    }

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme", 1) is not int v || v != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void Dispose()
    {
        _source?.Dispose();
        _source = null;
    }
}
