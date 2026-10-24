using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NetStrip.Core;
using NetStrip.Core.Logging;

namespace NetStrip;

public partial class App : Application
{
    private IDisposable? _instanceHandle;
    private AppHost? _host;
    private DispatcherTimer? _exitTimer;

    public static ILog Log { get; private set; } = null!;

    /// <summary>Raised on the UI thread when a second launch asks the running instance to open its flyout.</summary>
    public event Action? ActivationRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetStrip", "logs");
        Log = new FileLog(logDir, new SystemClock());
        InstallGlobalHandlers();

        var fake = e.Args.Contains("--fake", StringComparer.OrdinalIgnoreCase);
        var exitAfter = ExitAfterOption.Parse(e.Args, out var exitAfterIgnored);
        if (exitAfterIgnored)
            Log.Warn($"Ignoring {ExitAfterOption.Name}: expected a number of seconds above 0 and at most {ExitAfterOption.Max.TotalSeconds}.");

        if (!SingleInstance.TryAcquire(out var handle))
        {
            Log.Info("Second instance detected; signalled the first and exiting.");
            Shutdown();
            return;
        }
        _instanceHandle = handle;
        SingleInstance.Activated += () => Dispatcher.BeginInvoke(() => ActivationRequested?.Invoke());

        Log.Info($"NetStrip starting (fake={fake}).");
        SessionEnding += (_, args) =>
        {
            Log.Info($"Session ending ({args.ReasonSessionEnding}); saving usage.");
            _host?.SaveUsage();
        };

        // A few lines of text redrawn once a second do not need the GPU, and hardware rendering loads Direct3D and the
        // display driver (tens of MB and many driver threads) into the process. Must be set before any window exists.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        try
        {
            _host = new AppHost(this, Log, fake);
            _host.Start();
            if (exitAfter is { } delay) ScheduleExit(delay);
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed; exiting", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Strips, sampler and usage save happen inside AppHost.Dispose, in that order.
        _host?.Dispose();
        _host = null;
        // FileLog appends synchronously, so there is nothing buffered to flush; this is the last line written.
        Log?.Info($"NetStrip exiting (code {e.ApplicationExitCode}).");
        try { _instanceHandle?.Dispose(); }
        catch (ApplicationException) { /* mutex not owned by this thread */ }
        base.OnExit(e);
    }

    /// <summary>
    /// For <c>--exit-after</c>: shuts down through <see cref="Application.Shutdown()"/> once <paramref name="delay"/> has
    /// passed, so <see cref="OnExit"/> disposes AppHost, saves usage and logs the exit exactly as a user's Exit would.
    /// </summary>
    private void ScheduleExit(TimeSpan delay)
    {
        Log.Info($"Will exit by itself in {delay.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s ({ExitAfterOption.Name}).");
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log.Info($"{ExitAfterOption.Name} elapsed; shutting down.");
            Shutdown();
        };
        timer.Start();
        _exitTimer = timer;
    }

    private void InstallGlobalHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled dispatcher exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"Unhandled exception (terminating={args.IsTerminating})", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}
