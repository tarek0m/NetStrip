namespace NetStrip.Core;

/// <summary>Reading the app's JSON files without mistaking a briefly locked file (antivirus, backup, sync) for a corrupt one.</summary>
internal static class FileReads
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150)];

    /// <summary>
    /// <see cref="IFileSystem.ReadAllText"/>, retried briefly on access errors. Returns null if the file is missing;
    /// throws if it still cannot be read, in which case its contents are unknown and the file must be left alone.
    /// </summary>
    public static string? ReadWithRetry(IFileSystem fs, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return fs.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < RetryDelays.Length)
            {
                Thread.Sleep(RetryDelays[attempt]);
            }
        }
    }
}
