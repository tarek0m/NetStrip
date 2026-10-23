using NetStrip.Core.Logging;
using NetStrip.Core.Tests.Fakes;

namespace NetStrip.Core.Tests.Logging;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netstrip-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WritesToDatedFile()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var log = new FileLog(_dir, clock);

        log.Info("hi");

        var text = File.ReadAllText(Path.Combine(_dir, "netstrip-2026-10-23.log"));
        Assert.Contains("hi", text);
    }

    [Fact]
    public void KeepsSevenFiles()
    {
        Directory.CreateDirectory(_dir);
        for (var day = 1; day <= 9; day++)
            File.WriteAllText(Path.Combine(_dir, $"netstrip-2026-10-{day:00}.log"), "old");
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));

        _ = new FileLog(_dir, clock);

        var names = Directory.GetFiles(_dir, "netstrip-*.log").Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(7, names.Length);
        Assert.Equal("netstrip-2026-10-03.log", names[0]);
        Assert.Equal("netstrip-2026-10-09.log", names[^1]);
    }
}
