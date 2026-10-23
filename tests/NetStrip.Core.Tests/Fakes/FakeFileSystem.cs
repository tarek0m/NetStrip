using NetStrip.Core;

namespace NetStrip.Core.Tests.Fakes;

public sealed class FakeFileSystem : IFileSystem
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, string Contents)> AtomicWrites { get; } = new();
    public List<(string From, string To)> Moves { get; } = new();

    /// <summary>While positive, each read throws IOException (a locked file) and decrements it.</summary>
    public int FailingReads { get; set; }

    public string? ReadAllText(string path)
    {
        if (FailingReads > 0)
        {
            FailingReads--;
            throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
        }
        return Files.TryGetValue(path, out var text) ? text : null;
    }

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
