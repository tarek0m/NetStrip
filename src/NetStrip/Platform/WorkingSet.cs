using Windows.Win32;

namespace NetStrip.Platform;

/// <summary>
/// Hands pages the app no longer touches (startup code, XAML parsing, a closed flyout) back to the OS. Steady-state
/// work faults its few pages straight back in; everything else stays out until it is used again.
/// </summary>
internal static class WorkingSet
{
    /// <summary>Compacts the GC heap, returns its free memory to the OS, then empties the working set. Returns false if the OS refused.</summary>
    public static bool Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        // (SIZE_T)-1 for both bounds means "remove as many pages as possible" (SetProcessWorkingSetSizeEx docs).
        return PInvoke.SetProcessWorkingSetSizeEx(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue, 0);
    }
}
