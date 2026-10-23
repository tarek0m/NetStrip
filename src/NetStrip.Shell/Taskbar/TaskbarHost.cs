using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using NetStrip.Core;
using NetStrip.Core.Layout;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NetStrip.Shell.Taskbar;

/// <summary>
/// Attaches one strip surface per taskbar in <see cref="Scope"/>, keeps it positioned, re-attaches after Explorer
/// restarts, and falls back to floating mode per <see cref="AttachRetryPolicy"/> (spec §6).
/// UI-thread only. Win32 failures are logged and retried; nothing throws out of hooks or timers (spec §12).
/// </summary>
/// <remarks>
/// Surface contract: <see cref="IStripSurface.Handle"/> should be valid right after <c>createSurface</c> returns
/// (e.g. <c>WindowInteropHelper.EnsureHandle()</c>), and <see cref="IStripSurface.Apply"/> must position the window
/// synchronously (child coordinates for Full/Compact). The host owns the surfaces it creates: those that implement
/// <see cref="IDisposable"/> are disposed when dropped (scope change, taskbar gone) and on <see cref="Dispose"/>.
/// When Explorer exits, the strip windows parented into its taskbars are destroyed with them; the host then asks
/// <c>createSurface</c> for replacements, so consumers should read <see cref="Surfaces"/> rather than cache them.
/// </remarks>
public sealed class TaskbarHost(Func<string, IStripSurface> createSurface, ILog log) : IDisposable
{
    private const int PaddingDip = 4;
    private const int DefaultTaskbarThicknessDip = 48;
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);
    private const int MaxAttachFailures = 3;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<string, Attachment> _attachments = new();
    private readonly Dictionary<string, IStripSurface> _surfaces = new();
    private readonly Dictionary<nint, ProbeResult> _probeResults = new();
    private readonly AttachRetryPolicy _policy = new(StartupGrace, MaxAttachFailures);
    private readonly Stopwatch _sinceStart = new();   // restarted when Explorer restarts: a fresh grace window

    private Dispatcher? _dispatcher;
    private DispatcherTimer? _debounce;
    private DispatcherTimer? _maintenance;
    private HwndSource? _messageWindow;
    private uint _taskbarCreatedMessage;
    private WINEVENTPROC? _winEventProc;              // rooted for the lifetime of the hook (GC safety)
    private HWINEVENTHOOK _hook;
    private uint _hookedPid;
    private MonitorScope _scope = MonitorScope.Primary;
    private bool _started;
    private bool _disposed;
    private bool _busy;
    private bool _inFallback;
    private bool _probeRunning;
    private bool _probeAgain;
    private bool _probeFailureLogged;
    private string? _lastFailure;

    /// <summary>Raised once each time the host enters floating mode.</summary>
    public event Action? FallbackActivated;

    /// <summary>Current surfaces by taskbar key ("primary" or a monitor device name).</summary>
    public IReadOnlyDictionary<string, IStripSurface> Surfaces => _surfaces;

    /// <summary>Which taskbars get a strip. Changing it re-attaches immediately once started.</summary>
    public MonitorScope Scope
    {
        get => _scope;
        set
        {
            if (_scope == value) return;
            _scope = value;
            if (_started && !_disposed) AttachAll("monitor scope changed");
        }
    }

    /// <summary>Starts attaching. Must be called on the UI thread (it owns the timers, hook and message window).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _sinceStart.Restart();

        var debounce = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = DebounceDelay };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            try
            {
                Reposition();
            }
            catch (Exception ex)
            {
                log.Warn("TaskbarHost: reposition failed", ex);
            }
        };
        _debounce = debounce;
        _maintenance = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _maintenance.Tick += (_, _) => OnMaintenanceTick();
        _winEventProc = OnWinEvent;

        try
        {
            CreateMessageWindow();
        }
        catch (Exception ex)
        {
            log.Error("TaskbarHost: could not create the message window; Explorer restarts will be caught by the health check", ex);
        }

        AttachAll("start");
    }

    /// <summary>Re-measures and re-places every surface (call on settings/theme change).</summary>
    public void Reposition()
    {
        if (!_started || _disposed) return;
        RepositionCore();
        RequestProbe();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Stop();
        _maintenance?.Stop();
        Unhook();
        try
        {
            if (_messageWindow is not null)
            {
                _messageWindow.RemoveHook(WndProc);
                _messageWindow.Dispose();
                _messageWindow = null;
            }
        }
        catch (Exception ex)
        {
            log.Warn("TaskbarHost: disposing the message window failed", ex);
        }
        foreach (var attachment in _attachments.Values) DisposeSurface(attachment.Surface);
        _attachments.Clear();
        _surfaces.Clear();
        _winEventProc = null;
    }

    // ---- Attach / fallback -------------------------------------------------------------------------------

    private void AttachAll(string reason)
    {
        if (!_started || _disposed) return;
        if (_busy)
        {
            Schedule(BusyRetryDelay);
            return;
        }

        bool ok;
        _busy = true;
        try
        {
            ok = AttachPass();
        }
        catch (Exception ex)
        {
            log.Error($"TaskbarHost: attach pass ({reason}) failed", ex);
            ok = false;
        }
        finally
        {
            _busy = false;
        }

        var sinceStart = _sinceStart.Elapsed;
        if (ok)
        {
            _policy.RecordSuccess();
            _lastFailure = null;
            if (_inFallback)
            {
                _inFallback = false;
                log.Info("TaskbarHost: attached to the taskbar again; leaving floating mode");
            }
            Schedule(HealthCheckInterval);
            RequestProbe();
            return;
        }

        _policy.RecordFailure(sinceStart);
        if (_policy.ShouldFallBack) EnterFallback();
        Schedule(_policy.NextRetryDelay(sinceStart));
    }

    /// <summary>One attempt to attach every taskbar in scope. True only if the primary taskbar and all others attached.</summary>
    private bool AttachPass()
    {
        var found = TaskbarLocator.Find(_scope);

        // Drop surfaces whose taskbar is gone or out of scope. The primary one stays: it floats if Explorer is missing.
        foreach (var key in _attachments.Keys.ToList())
        {
            if (key != TaskbarKeys.Primary && !found.Exists(t => t.Key == key)) Drop(key);
        }

        var ok = true;
        if (!found.Exists(t => t.Key == TaskbarKeys.Primary))
        {
            ok = Fail("Shell_TrayWnd not found (Explorer not running yet?)");
        }

        foreach (var taskbar in found)
        {
            ok &= TryAttach(taskbar);
        }

        EnsureHook(found);
        return ok;
    }

    private bool TryAttach(TaskbarHandle taskbar)
    {
        var attachment = GetOrCreate(taskbar.Key);
        if (attachment is null) return false;

        var surface = (HWND)attachment.Surface.Handle;
        if (!IsOurWindow(surface)) return Fail($"{taskbar.Key}: strip window has no live handle");
        attachment.KnownHandle = surface;

        if (attachment.Attached && attachment.Taskbar == taskbar.Hwnd && IsChildOf(surface, taskbar.Hwnd)) return true;

        var located = TaskbarLocator.TryLocate(taskbar);
        if (located is null) return Fail($"{taskbar.Key}: taskbar geometry unavailable");

        // Per SetParent docs: switch to WS_CHILD before parenting a top-level window.
        SetChildStyle(surface, child: true);
        Marshal.SetLastPInvokeError(0);
        var previous = PInvoke.SetParent(surface, taskbar.Hwnd);
        if (previous.IsNull && Marshal.GetLastPInvokeError() is var error and not 0)
        {
            SetChildStyle(surface, child: false);
            return Fail($"{taskbar.Key}: SetParent failed (error {error})");
        }

        attachment.Taskbar = taskbar.Hwnd;
        attachment.Attached = false;
        attachment.Floating = false;
        attachment.Last = null;
        ApplyAttached(attachment, located);

        if (!IsChildOf(surface, taskbar.Hwnd)) return Fail($"{taskbar.Key}: strip is not a child of the taskbar after SetParent");
        if (!IsInside(surface, taskbar.Hwnd)) return Fail($"{taskbar.Key}: strip ended up outside the taskbar rect");

        attachment.Attached = true;
        log.Info($"TaskbarHost: attached strip to taskbar '{taskbar.Key}' ({attachment.Last?.Mode} mode)");
        return true;
    }

    private void EnterFallback()
    {
        // Make sure there is a primary surface to float even when Explorer's taskbar does not exist.
        GetOrCreate(TaskbarKeys.Primary);
        foreach (var attachment in _attachments.Values)
        {
            if (attachment.Attached) continue;
            try
            {
                Float(attachment);
            }
            catch (Exception ex)
            {
                log.Warn($"TaskbarHost: floating the '{attachment.Key}' strip failed", ex);
            }
        }

        if (_inFallback) return;
        _inFallback = true;
        log.Warn($"TaskbarHost: could not attach to the taskbar {MaxAttachFailures} times in a row; switching to floating mode");
        try
        {
            FallbackActivated?.Invoke();
        }
        catch (Exception ex)
        {
            log.Error("TaskbarHost: FallbackActivated handler threw", ex);
        }
    }

    private void Float(Attachment attachment)
    {
        var surface = (HWND)attachment.Surface.Handle;
        if (IsOurWindow(surface) && !PInvoke.GetParent(surface).IsNull)
        {
            PInvoke.SetParent(surface, HWND.Null);
            SetChildStyle(surface, child: false);
            attachment.Last = null;
        }
        attachment.Attached = false;
        attachment.Floating = true;

        var located = attachment.Taskbar.IsNull ? null : TaskbarLocator.TryLocate(new TaskbarHandle(attachment.Key, attachment.Taskbar));
        if (located is null && attachment.Key == TaskbarKeys.Primary)
        {
            var primary = TaskbarLocator.Find(MonitorScope.Primary);
            if (primary.Count > 0) located = TaskbarLocator.TryLocate(primary[0]);
        }

        double dpi;
        int thickness;
        PixelRect workArea;
        PixelRect? tray;
        TaskbarEdge edge;
        if (located is not null)
        {
            var g = located.Geometry;
            dpi = g.DpiScale;
            thickness = StripLayout.IsVertical(g.Edge) ? g.Taskbar.Width : g.Taskbar.Height;
            workArea = located.WorkArea;
            tray = g.Tray;
            edge = g.Edge;
        }
        else
        {
            var windowDpi = IsOurWindow(surface) ? PInvoke.GetDpiForWindow(surface) : 0;
            dpi = windowDpi == 0 ? 1.0 : windowDpi / 96.0;
            thickness = (int)Math.Round(DefaultTaskbarThicknessDip * dpi);
            workArea = PrimaryWorkArea();
            tray = null;
            edge = TaskbarEdge.Bottom;
        }

        var size = attachment.Surface.Measure(StripMode.Floating, dpi, thickness);
        var rect = StripLayout.PlaceFloating(workArea, tray, edge, size, Padding(dpi));
        ApplyIfChanged(attachment, new StripPlacement(0, rect, StripMode.Floating, dpi));
    }

    // ---- Positioning -------------------------------------------------------------------------------------

    private void RepositionCore()
    {
        if (_busy) return;
        var needsAttach = false;
        _busy = true;
        try
        {
            foreach (var attachment in _attachments.Values)
            {
                try
                {
                    if (attachment.Attached)
                    {
                        var surface = (HWND)attachment.Surface.Handle;
                        var located = IsOurWindow(surface) && IsChildOf(surface, attachment.Taskbar)
                            ? TaskbarLocator.TryLocate(new TaskbarHandle(attachment.Key, attachment.Taskbar))
                            : null;
                        if (located is null)
                        {
                            attachment.Attached = false;
                            needsAttach = true;
                            continue;
                        }
                        ApplyAttached(attachment, located);
                    }
                    else if (attachment.Floating)
                    {
                        Float(attachment);
                    }
                }
                catch (Exception ex)
                {
                    log.Warn($"TaskbarHost: repositioning the '{attachment.Key}' strip failed", ex);
                }
            }
        }
        finally
        {
            _busy = false;
        }

        if (needsAttach)
        {
            NoteShellRestartIfTaskbarGone();
            AttachAll("strip lost its taskbar");
        }
    }

    private void ApplyAttached(Attachment attachment, LocatedTaskbar located)
    {
        var g = located.Geometry;
        _probeResults.TryGetValue(located.Hwnd, out var probe);
        g = g with { TaskList = probe.TaskList, Tray = g.Tray ?? probe.ClockArea };

        var thickness = StripLayout.IsVertical(g.Edge) ? g.Taskbar.Width : g.Taskbar.Height;
        var full = attachment.Surface.Measure(StripMode.Full, g.DpiScale, thickness);
        var compact = attachment.Surface.Measure(StripMode.Compact, g.DpiScale, thickness);
        var (bounds, mode) = StripLayout.Place(g, full, compact, Padding(g.DpiScale));

        var screen = bounds with { X = bounds.X + g.Taskbar.X, Y = bounds.Y + g.Taskbar.Y };
        var stillOverlaps = mode == StripMode.Compact && g.TaskList is { } list && StripLayout.Intersects(screen, list);
        if (stillOverlaps && !attachment.OverlapLogged)
            log.Info($"TaskbarHost: compact strip on '{attachment.Key}' still overlaps the taskbar buttons; keeping it compact");
        attachment.OverlapLogged = stillOverlaps;

        // Layout is relative to the taskbar's window rect; StripPlacement wants the parent's client coordinates.
        var client = ClientOrigin(located.Hwnd, g.Taskbar);
        var relative = screen with { X = screen.X - client.X, Y = screen.Y - client.Y };
        ApplyIfChanged(attachment, new StripPlacement(located.Hwnd, relative, mode, g.DpiScale));
    }

    private static void ApplyIfChanged(Attachment attachment, StripPlacement placement)
    {
        if (attachment.Last == placement) return;
        attachment.Surface.Apply(placement);
        attachment.Last = placement;
    }

    // ---- Timers, hook, shell messages --------------------------------------------------------------------

    private void OnMaintenanceTick()
    {
        _maintenance?.Stop();
        try
        {
            if (NeedsAttachPass()) AttachAll("retry/health check");
            else Schedule(HealthCheckInterval);
        }
        catch (Exception ex)
        {
            log.Warn("TaskbarHost: health check failed", ex);
            Schedule(_policy.NextRetryDelay(_sinceStart.Elapsed));
        }
    }

    private bool NeedsAttachPass()
    {
        if (_inFallback || _attachments.Count == 0) return true;
        foreach (var attachment in _attachments.Values)
        {
            if (!attachment.Attached) return true;
            if (!IsOurWindow((HWND)attachment.Surface.Handle) || !IsChildOf((HWND)attachment.Surface.Handle, attachment.Taskbar))
            {
                NoteShellRestartIfTaskbarGone();
                return true;
            }
        }

        // New or removed secondary taskbars (monitor plugged/unplugged, taskbar shown on all displays).
        var found = TaskbarLocator.Find(_scope);
        if (found.Count != _attachments.Count) return true;
        foreach (var taskbar in found)
        {
            if (!_attachments.TryGetValue(taskbar.Key, out var attachment) || attachment.Taskbar != taskbar.Hwnd) return true;
        }
        return false;
    }

    /// <summary>If an attached taskbar window no longer exists, Explorer is restarting: give it a fresh startup grace.</summary>
    private void NoteShellRestartIfTaskbarGone()
    {
        foreach (var attachment in _attachments.Values)
        {
            if (!attachment.Taskbar.IsNull && !PInvoke.IsWindow(attachment.Taskbar))
            {
                log.Info("TaskbarHost: taskbar window disappeared (Explorer restarting?); waiting for it to come back");
                _sinceStart.Restart();
                foreach (var a in _attachments.Values)
                {
                    a.Attached = false;
                    a.Taskbar = HWND.Null;
                }
                return;
            }
        }
    }

    private void Schedule(TimeSpan delay)
    {
        if (_maintenance is null || _disposed) return;
        _maintenance.Stop();
        _maintenance.Interval = delay;
        _maintenance.Start();
    }

    private void RequestReposition()
    {
        if (_debounce is null || _disposed) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void EnsureHook(List<TaskbarHandle> found)
    {
        if (_winEventProc is null || found.Count == 0 || found[0].Key != TaskbarKeys.Primary) return;
        PInvoke.GetWindowThreadProcessId(found[0].Hwnd, out var pid);
        if (pid == 0 || pid == _hookedPid) return;

        Unhook();
        _hook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
            HMODULE.Null, _winEventProc, pid, 0, PInvoke.WINEVENT_OUTOFCONTEXT);
        if (_hook.IsNull)
        {
            log.Warn("TaskbarHost: SetWinEventHook failed; will retry on the next attach pass");
            return;
        }
        _hookedPid = pid;
    }

    private void Unhook()
    {
        if (!_hook.IsNull)
        {
            try
            {
                PInvoke.UnhookWinEvent(_hook);
            }
            catch (Exception ex)
            {
                log.Warn("TaskbarHost: UnhookWinEvent failed", ex);
            }
        }
        _hook = HWINEVENTHOOK.Null;
        _hookedPid = 0;
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            if (_disposed || hwnd.IsNull || idObject == (int)OBJECT_IDENTIFIER.OBJID_CURSOR) return;
            foreach (var attachment in _attachments.Values)
            {
                var taskbar = attachment.Taskbar;
                if (taskbar.IsNull) continue;
                if (hwnd == taskbar || PInvoke.IsChild(taskbar, hwnd))
                {
                    RequestReposition();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("TaskbarHost: WinEvent callback failed", ex);
        }
    }

    private void CreateMessageWindow()
    {
        _taskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
        // A hidden top-level window, not HWND_MESSAGE: message-only windows do not receive broadcasts
        // (TaskbarCreated, WM_SETTINGCHANGE, WM_DISPLAYCHANGE).
        var parameters = new HwndSourceParameters("NetStrip.TaskbarHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)WINDOW_STYLE.WS_POPUP),
            ExtendedWindowStyle = (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            var message = unchecked((uint)msg);
            if (_disposed) return IntPtr.Zero;

            if (message == _taskbarCreatedMessage && message != 0)
            {
                log.Info("TaskbarHost: TaskbarCreated received (Explorer restarted); re-attaching");
                _sinceStart.Restart();
                foreach (var a in _attachments.Values) a.Attached = false;
                _dispatcher?.BeginInvoke(DispatcherPriority.Normal, () => AttachAll("TaskbarCreated"));
            }
            else if (message == PInvoke.WM_DISPLAYCHANGE)
            {
                _dispatcher?.BeginInvoke(DispatcherPriority.Normal, () => AttachAll("display change"));
            }
            else if (message == PInvoke.WM_DPICHANGED || message == PInvoke.WM_SETTINGCHANGE)
            {
                // Includes TaskbarAl (alignment) and ImmersiveColorSet changes: re-measure and reposition.
                RequestReposition();
            }
        }
        catch (Exception ex)
        {
            log.Warn("TaskbarHost: message handling failed", ex);
        }
        return IntPtr.Zero;
    }

    // ---- Task-list probe (UI Automation, off the UI thread) ----------------------------------------------

    private void RequestProbe()
    {
        if (_disposed || _dispatcher is null) return;
        if (_probeRunning)
        {
            _probeAgain = true;
            return;
        }

        var targets = new List<(nint Hwnd, bool WantClock)>(_attachments.Count);
        foreach (var attachment in _attachments.Values)
        {
            if (!attachment.Taskbar.IsNull) targets.Add((attachment.Taskbar, attachment.Key != TaskbarKeys.Primary));
        }
        if (targets.Count == 0) return;

        _probeRunning = true;
        var dispatcher = _dispatcher;
        Task.Run(() =>
        {
            var results = new List<(nint Hwnd, ProbeResult Result, Exception? Error)>(targets.Count);
            foreach (var (hwnd, wantClock) in targets)
            {
                var result = TaskListProbe.Probe(hwnd, wantClock, out var error);
                results.Add((hwnd, result, error));
            }
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => OnProbeCompleted(results));
        });
    }

    private void OnProbeCompleted(List<(nint Hwnd, ProbeResult Result, Exception? Error)> results)
    {
        _probeRunning = false;
        if (_disposed) return;

        try
        {
            var changed = false;
            foreach (var (hwnd, result, error) in results)
            {
                if (error is not null)
                {
                    if (!_probeFailureLogged) log.Warn("TaskbarHost: task-list probe failed; overlap detection is off until it succeeds", error);
                    _probeFailureLogged = true;
                    continue;
                }
                _probeFailureLogged = false;
                if (!_probeResults.TryGetValue(hwnd, out var old) || old != result)
                {
                    _probeResults[hwnd] = result;
                    changed = true;
                }
            }

            foreach (var stale in _probeResults.Keys.Where(h => !_attachments.Values.Any(a => (nint)a.Taskbar == h)).ToList())
                _probeResults.Remove(stale);

            if (changed) RepositionCore();
        }
        catch (Exception ex)
        {
            log.Warn("TaskbarHost: applying probe results failed", ex);
        }

        if (_probeAgain)
        {
            _probeAgain = false;
            RequestProbe();
        }
    }

    // ---- Surfaces ---------------------------------------------------------------------------------------

    private Attachment? GetOrCreate(string key)
    {
        if (_attachments.TryGetValue(key, out var existing))
        {
            // A handle we saw before that is no longer ours: the window died with its parent (Explorer restart).
            if (existing.KnownHandle.IsNull || IsOurWindow((HWND)existing.Surface.Handle)) return existing;
            log.Info($"TaskbarHost: strip window for '{key}' was destroyed; creating a new one");
            DisposeSurface(existing.Surface);
            _attachments.Remove(key);
            _surfaces.Remove(key);
        }

        IStripSurface surface;
        try
        {
            surface = createSurface(key);
        }
        catch (Exception ex)
        {
            Fail($"{key}: creating the strip window failed");
            log.Error($"TaskbarHost: createSurface('{key}') threw", ex);
            return null;
        }

        var attachment = new Attachment(key, surface);
        _attachments[key] = attachment;
        _surfaces[key] = surface;
        return attachment;
    }

    private void Drop(string key)
    {
        if (!_attachments.Remove(key, out var attachment)) return;
        _surfaces.Remove(key);
        log.Info($"TaskbarHost: removing strip for '{key}' (taskbar gone or out of scope)");
        try
        {
            var surface = (HWND)attachment.Surface.Handle;
            if (IsOurWindow(surface))
            {
                attachment.Surface.SetHidden(true);
                if (!PInvoke.GetParent(surface).IsNull)
                {
                    PInvoke.SetParent(surface, HWND.Null);
                    SetChildStyle(surface, child: false);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"TaskbarHost: detaching the '{key}' strip failed", ex);
        }
        DisposeSurface(attachment.Surface);
    }

    private void DisposeSurface(IStripSurface surface)
    {
        try
        {
            (surface as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warn("TaskbarHost: disposing a strip surface failed", ex);
        }
    }

    private bool Fail(string reason)
    {
        if (reason != _lastFailure) log.Warn($"TaskbarHost: attach failed: {reason}");
        _lastFailure = reason;
        return false;
    }

    // ---- Win32 helpers ----------------------------------------------------------------------------------

    private static int Padding(double dpiScale) => (int)Math.Round(PaddingDip * dpiScale);

    private static bool IsOurWindow(HWND hwnd)
    {
        if (hwnd.IsNull || !PInvoke.IsWindow(hwnd)) return false;
        PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static bool IsChildOf(HWND child, HWND parent) =>
        !parent.IsNull && PInvoke.IsWindow(parent) && PInvoke.GetParent(child) == parent;

    private static bool IsInside(HWND child, HWND parent)
    {
        if (!PInvoke.GetWindowRect(child, out var c) || !PInvoke.GetWindowRect(parent, out var p)) return false;
        return c.left >= p.left && c.top >= p.top && c.right <= p.right && c.bottom <= p.bottom;
    }

    private static void SetChildStyle(HWND hwnd, bool child)
    {
        var style = unchecked((uint)(long)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE));
        var updated = child
            ? (style | (uint)WINDOW_STYLE.WS_CHILD) & ~(uint)WINDOW_STYLE.WS_POPUP
            : (style | (uint)WINDOW_STYLE.WS_POPUP) & ~(uint)WINDOW_STYLE.WS_CHILD;
        if (updated == style) return;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)updated);
        PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    /// <summary>Screen position of the taskbar's client-area origin (falls back to its window origin).</summary>
    private static unsafe PixelPoint ClientOrigin(HWND taskbar, PixelRect windowRect)
    {
        var point = new System.Drawing.Point(0, 0);
        Marshal.SetLastPInvokeError(0);
        if (PInvoke.MapWindowPoints(taskbar, HWND.Null, &point, 1) == 0 && Marshal.GetLastPInvokeError() != 0)
            return new PixelPoint(windowRect.X, windowRect.Y);
        return new PixelPoint(point.X, point.Y);
    }

    private static PixelRect PrimaryWorkArea()
    {
        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        return TaskbarKeys.TryGetMonitor(monitor, TaskbarKeys.Primary, out var facts) ? facts.WorkArea : new PixelRect(0, 0, 1280, 720);
    }

    private sealed class Attachment(string key, IStripSurface surface)
    {
        public string Key { get; } = key;
        public IStripSurface Surface { get; } = surface;
        public HWND Taskbar { get; set; }
        public HWND KnownHandle { get; set; }
        public bool Attached { get; set; }
        public bool Floating { get; set; }
        public bool OverlapLogged { get; set; }
        public StripPlacement? Last { get; set; }
    }
}
