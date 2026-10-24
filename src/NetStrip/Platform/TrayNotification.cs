using System.Runtime.CompilerServices;
using System.Windows.Interop;
using System.Windows.Threading;
using NetStrip.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NetStrip.Platform;

/// <summary>
/// Shows a Windows notification without WinRT: a temporary notification-area icon with an info balloon
/// (<c>Shell_NotifyIcon</c> + <c>NIF_INFO</c>), which Windows 10 and 11 present as an ordinary notification under the
/// exe's FileDescription. The icon and its hidden owner window exist only while a notification is up: they go away when
/// the balloon times out, is dismissed or clicked, or after a fallback delay. Best effort: failures are logged, never
/// thrown. UI-thread only.
/// </summary>
internal sealed class TrayNotification : IDisposable
{
    private const uint CallbackMessage = PInvoke.WM_APP + 1;
    private const int WS_POPUP = unchecked((int)0x80000000);
    // Removal fallback for when no balloon event arrives (notifications off, Do not disturb): Windows' own display
    // time ("Show notifications for", 5 s by default, up to 5 min) plus a margin, but at least a minute.
    private static readonly TimeSpan MinFallback = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FallbackMargin = TimeSpan.FromSeconds(30);

    private readonly ILog _log;
    private HwndSource? _window;
    private DispatcherTimer? _fallback;
    private HICON _smallIcon;
    private HICON _largeIcon;
    private uint _iconId;          // new id per notification, so late events of a replaced icon are ignored
    private bool _iconAdded;
    private bool _version4;
    private bool _disposed;

    public TrayNotification(ILog log)
    {
        _log = log;
    }

    /// <summary>Raised when the notification (or its icon) is clicked; the icon is already gone by then.</summary>
    public event Action? Clicked;

    /// <summary>Shows <paramref name="text"/> under <paramref name="title"/>, replacing a notification still showing.</summary>
    public void Show(string title, string text)
    {
        if (_disposed) return;
        try
        {
            RemoveIcon();
            _window ??= CreateWindow();
            LoadIcons();
            _iconId = _iconId % 0xFFFF + 1;   // NOTIFYICON_VERSION_4 reports the id in a 16-bit HIWORD

            var data = NewData();
            data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON |
                          NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
            data.uCallbackMessage = CallbackMessage;
            data.hIcon = _smallIcon;
            data.szTip = Fit(title, 127);
            if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, data))
            {
                _log.Warn("Could not add the notification icon");
                RemoveIcon();
                return;
            }
            _iconAdded = true;

            // Version 4: balloon events carry the icon id in HIWORD(lParam). Set before the balloon so every event uses it.
            data.uVersion = PInvoke.NOTIFYICON_VERSION_4;
            _version4 = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, data);
            if (!_version4) _log.Warn("Could not set the notification icon version; using legacy callbacks");

            data.uVersion = 0;
            data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_INFO;
            data.szInfoTitle = Fit(title, 63);
            data.szInfo = Fit(text, 255);
            // NIIF_USER takes hBalloonIcon, or hIcon when that is null.
            data.hBalloonIcon = _largeIcon;
            data.dwInfoFlags = _largeIcon.IsNull
                ? _smallIcon.IsNull ? NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO : NOTIFY_ICON_INFOTIP_FLAGS.NIIF_USER
                : NOTIFY_ICON_INFOTIP_FLAGS.NIIF_USER | NOTIFY_ICON_INFOTIP_FLAGS.NIIF_LARGE_ICON;
            if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data))
            {
                _log.Warn("Could not show the notification balloon");
                RemoveIcon();
                return;
            }
            StartFallback();
        }
        catch (Exception ex)
        {
            _log.Warn("Showing the notification failed", ex);
            TryRemoveIcon();
        }
    }

    private HwndSource CreateWindow()
    {
        // Hidden top-level popup (like ThemeColors' listener) that receives the icon's callback messages.
        var p = new HwndSourceParameters("NetStripNotification") { Width = 0, Height = 0, WindowStyle = WS_POPUP };
        var window = new HwndSource(p);
        window.AddHook(WndProc);
        return window;
    }

    /// <summary>The exe's own icon (ApplicationIcon) at the system small and large icon sizes.</summary>
    private void LoadIcons()
    {
        if (!_smallIcon.IsNull || !_largeIcon.IsNull) return;
        if (Environment.ProcessPath is not { } exe) return;
        Span<HICON> large = stackalloc HICON[1];
        Span<HICON> small = stackalloc HICON[1];
        PInvoke.ExtractIconEx(exe, 0, large, small);
        _largeIcon = large[0];
        _smallIcon = small[0];
        if (_smallIcon.IsNull && _largeIcon.IsNull) _log.Warn($"Could not load the notification icon from {exe}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return IntPtr.Zero;
        handled = true;
        try
        {
            // Version 4: LOWORD(lParam) = event, HIWORD(lParam) = icon id. Legacy: lParam = event, wParam = icon id.
            var l = (ulong)lParam.ToInt64();
            var evt = (uint)(l & 0xFFFF);
            var id = _version4 ? (uint)((l >> 16) & 0xFFFF) : (uint)wParam.ToInt64();
            if (!_iconAdded || id != _iconId) return IntPtr.Zero;

            switch (evt)
            {
                case PInvoke.NIN_BALLOONUSERCLICK:
                case PInvoke.NIN_SELECT:
                case PInvoke.NIN_SELECT | PInvoke.NINF_KEY:
                    RemoveIcon();
                    Clicked?.Invoke();
                    break;
                case PInvoke.NIN_BALLOONTIMEOUT:
                case PInvoke.NIN_BALLOONHIDE:
                    RemoveIcon();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Handling a notification event failed", ex);
        }
        return IntPtr.Zero;
    }

    private void StartFallback()
    {
        if (_fallback is null)
        {
            _fallback = new DispatcherTimer(DispatcherPriority.Background);
            _fallback.Tick += (_, _) =>
            {
                _log.Info("Notification got no timeout event; removing its icon");
                TryRemoveIcon();
            };
        }
        _fallback.Interval = FallbackDelay();
        _fallback.Start();
    }

    private static unsafe TimeSpan FallbackDelay()
    {
        uint seconds = 0;
        PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETMESSAGEDURATION, 0, &seconds, 0);
        var shown = TimeSpan.FromSeconds(seconds) + FallbackMargin;
        return shown > MinFallback ? shown : MinFallback;
    }

    /// <summary>Deletes the icon (if any) and its handles; the window follows once the current message is done.</summary>
    private void RemoveIcon()
    {
        _fallback?.Stop();
        if (_iconAdded)
        {
            _iconAdded = false;   // first: deleting can deliver NIN_BALLOONHIDE re-entrantly
            if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, NewData()))
                _log.Warn("Could not remove the notification icon");
        }
        DestroyIcons();
        // Not from inside its own window procedure: release the window on a later dispatcher turn, unless reused.
        _window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, ReleaseWindowIfIdle);
    }

    private void TryRemoveIcon()
    {
        try
        {
            RemoveIcon();
        }
        catch (Exception ex)
        {
            _log.Warn("Removing the notification icon failed", ex);
        }
    }

    private void ReleaseWindowIfIdle()
    {
        if (_iconAdded || _window is null) return;
        _window.Dispose();
        _window = null;
    }

    private void DestroyIcons()
    {
        if (!_smallIcon.IsNull) PInvoke.DestroyIcon(_smallIcon);
        if (!_largeIcon.IsNull) PInvoke.DestroyIcon(_largeIcon);
        _smallIcon = default;
        _largeIcon = default;
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)Unsafe.SizeOf<NOTIFYICONDATAW>(),
        hWnd = new(_window?.Handle ?? IntPtr.Zero),
        uID = _iconId,
    };

    /// <summary>Truncates to the fixed buffer's length, leaving room for the terminating null.</summary>
    private static string Fit(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clicked = null;
        TryRemoveIcon();
        _window?.Dispose();
        _window = null;
    }
}
