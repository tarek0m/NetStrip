using System.IO;
using System.Windows;
using NetStrip.Core;
using NetStrip.Core.Logging;

namespace NetStrip;

public partial class App : Application
{
    private IDisposable? _instanceHandle;
    private AppHost? _host;

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

        try
        {
            _host = new AppHost(this, Log, fake);
            _host.Start();
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
