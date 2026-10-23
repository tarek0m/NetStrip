namespace NetStrip;

/// <summary>
/// Single-instance guard. The first process owns the <c>Local\NetStrip</c> mutex and listens on the
/// <c>Local\NetStrip.Activate</c> event; later launches signal that event and exit.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\NetStrip";
    private const string ActivateEventName = @"Local\NetStrip.Activate";

    /// <summary>Raised (on a thread-pool thread) when a second instance asks the first to open its flyout.</summary>
    public static event Action? Activated;

    /// <returns>true when this process is the first instance; <paramref name="handle"/> then keeps the guard alive until disposed.</returns>
    public static bool TryAcquire(out IDisposable handle)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalFirstInstance();
            handle = NullHandle.Instance;
            return false;
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            activate, static (_, _) => Activated?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        handle = new Handle(mutex, activate, registration);
        return true;
    }

    private static void SignalFirstInstance()
    {
        try
        {
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
            activate.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // First instance is still starting up; nothing to activate yet.
        }
    }

    private sealed class NullHandle : IDisposable
    {
        public static readonly NullHandle Instance = new();
        public void Dispose() { }
    }

    private sealed class Handle(Mutex mutex, EventWaitHandle activate, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            activate.Dispose();
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
