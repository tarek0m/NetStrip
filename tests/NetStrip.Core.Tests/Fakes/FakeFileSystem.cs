using NetStrip.Core;

namespace NetStrip.Core.Tests.Fakes;

public sealed class FakeFileSystem : IFileSystem
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, string Contents)> AtomicWrites { get; } = new();
    public List<(string From, string To)> Moves { get; } = new();

    public string? ReadAllText(string path) => Files.TryGetValue(path, out var text) ? text : null;

    public void WriteAllTextAtomic(string path, string contents)
    {
        AtomicWrites.Add((path, contents));
        Files[path] = contents;
    }

    public void Move(string from, string to)
    {
        Moves.Add((from, to));
        if (!Files.Remove(from, out var text)) throw new FileNotFoundException(from);
        Files[to] = text;
    }
}
