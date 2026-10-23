using System.IO;
using System.Windows;
using System.Windows.Threading;
using NetStrip.Core;
using NetStrip.Core.Logging;
using NetStrip.Core.Presentation;
using NetStrip.Theme;

namespace NetStrip;

public partial class App : Application
{
    private IDisposable? _instanceHandle;
    private FakeSampleSource? _fake;
    private ThemeColors? _theme;

    public static ILog Log { get; private set; } = null!;
    public static bool FakeMode { get; private set; }
    public static bool FloatingDebug { get; private set; }

    /// <summary>Raised on the UI thread when a second launch asks the running instance to open its flyout.</summary>
    public event Action? ActivationRequested;

    public ThemeColors Theme => _theme!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetStrip", "logs");
        Log = new FileLog(logDir, new SystemClock());
        InstallGlobalHandlers();

        FakeMode = e.Args.Contains("--fake", StringComparer.OrdinalIgnoreCase);
        FloatingDebug = e.Args.Contains("--floating", StringComparer.OrdinalIgnoreCase);

        if (!SingleInstance.TryAcquire(out var handle))
        {
            Log.Info("Second instance detected; signalled the first and exiting.");
            Shutdown();
            return;
        }
        _instanceHandle = handle;
        SingleInstance.Activated += () => Dispatcher.BeginInvoke(() => ActivationRequested?.Invoke());

        _theme = new ThemeColors();
        _theme.StartListening();

        Log.Info($"NetStrip starting (fake={FakeMode}, floating={FloatingDebug}).");

        // Temporary wiring; Task 13 replaces this with AppHost composition.
        if (FakeMode)
        {
            _fake = new FakeSampleSource();
            _fake.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _fake?.Dispose();
        _theme?.Dispose();
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
