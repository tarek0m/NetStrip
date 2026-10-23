using System.Collections.Concurrent;
using NetStrip.Core;

namespace NetStrip.Fakes;

/// <summary>
/// Keeps files in memory so <c>--fake</c> runs can use the real <c>UsageStore</c> without touching the user's usage.json.
/// </summary>
internal sealed class InMemoryFileSystem : IFileSystem
{
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public string? ReadAllText(string path) => _files.TryGetValue(path, out var text) ? text : null;

    public void WriteAllTextAtomic(string path, string contents) => _files[path] = contents;

    public void Move(string from, string to)
    {
        if (_files.TryRemove(from, out var text)) _files[to] = text;
    }
}
