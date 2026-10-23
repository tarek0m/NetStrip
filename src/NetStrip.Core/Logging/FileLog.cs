using System.Globalization;

namespace NetStrip.Core.Logging;

/// <summary>
/// Appends to <c>netstrip-yyyy-MM-dd.log</c> in the given directory; keeps the newest 7 files, pruning at startup and
/// whenever the date rolls over (the app runs for weeks). Never throws.
/// </summary>
public sealed class FileLog : ILog
{
    private const int KeepFiles = 7;

    private readonly string _directory;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private string? _currentPath;

    public FileLog(string directory, IClock clock)
    {
        _directory = directory;
        _clock = clock;
        Prune();
    }

    public void Info(string msg) => Write("INFO", msg, null);
    public void Warn(string msg, Exception? ex = null) => Write("WARN", msg, ex);
    public void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);

    private void Write(string level, string msg, Exception? ex)
    {
        var now = _clock.Now;
        var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}");
        if (ex is not null) line += Environment.NewLine + ex;
        line += Environment.NewLine;

        var path = Path.Combine(_directory, $"netstrip-{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(path, line);
                if (path != _currentPath)
                {
                    if (_currentPath is not null) Prune();   // a new day's file now exists
                    _currentPath = path;
                }
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }

    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            var files = Directory.GetFiles(_directory, "netstrip-*.log");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase); // date in name => lexical == chronological
            for (var i = 0; i < files.Length - KeepFiles; i++)
            {
                try { File.Delete(files[i]); }
                catch (Exception) { }
            }
        }
        catch (Exception)
        {
        }
    }
}
