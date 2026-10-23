using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Controls;

namespace NetStrip.Platform;

internal static class Mica
{
    private const int MinBuild = 22621;                  // DWMWA_SYSTEMBACKDROP_TYPE needs Windows 11 22H2
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= MinBuild;

    /// <summary>
    /// Applies the Mica backdrop (and rounded corners) when supported and returns true; otherwise leaves the
    /// window untouched so its themed solid background stays. Mica is skipped in high contrast. Call after the
    /// window handle exists. Use <see cref="RefreshDarkMode"/> afterwards when the theme changes.
    /// </summary>
    public static unsafe bool Apply(Window window)
    {
        if (!IsSupported || SystemParameters.HighContrast) return false;
        var helper = new WindowInteropHelper(window);
        var handle = helper.EnsureHandle();
        var hwnd = new HWND(handle);

        int backdrop = DWMSBT_MAINWINDOW;
        int corners = DWMWCP_ROUND;
        SetDarkMode(hwnd);
        PInvoke.DwmSetWindowAttribute(hwnd, (DWMWINDOWATTRIBUTE)DWMWA_WINDOW_CORNER_PREFERENCE, &corners, sizeof(int));
        var hr = PInvoke.DwmSetWindowAttribute(hwnd, (DWMWINDOWATTRIBUTE)DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(int));
        if (hr.Failed) return false;

        // Let the backdrop show through: transparent WPF surface and glass over the whole client area.
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        PInvoke.DwmExtendFrameIntoClientArea(hwnd, in margins);
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent;
        return true;
    }

    /// <summary>Re-syncs the backdrop tint (immersive dark mode) with the current app theme. Cheap; call on every show and theme change.</summary>
    public static void RefreshDarkMode(Window window) =>
        SetDarkMode(new HWND(new WindowInteropHelper(window).EnsureHandle()));

    /// <summary>Turns Mica off again (for example when high contrast was enabled) and restores the themed solid background.</summary>
    public static unsafe void Remove(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var hwnd = new HWND(handle);
        int none = DWMSBT_NONE;
        PInvoke.DwmSetWindowAttribute(hwnd, (DWMWINDOWATTRIBUTE)DWMWA_SYSTEMBACKDROP_TYPE, &none, sizeof(int));
        var margins = new MARGINS();
        PInvoke.DwmExtendFrameIntoClientArea(hwnd, in margins);
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor = SystemColors.WindowColor;
        window.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
    }

    private static unsafe void SetDarkMode(HWND hwnd)
    {
        int dark = IsAppDark() ? 1 : 0;
        PInvoke.DwmSetWindowAttribute(hwnd, (DWMWINDOWATTRIBUTE)DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int));
    }

    private static bool IsAppDark()
    {
        var app = Application.Current;
        if (app is not null)
        {
            if (app.ThemeMode == ThemeMode.Dark) return true;
            if (app.ThemeMode == ThemeMode.Light) return false;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
