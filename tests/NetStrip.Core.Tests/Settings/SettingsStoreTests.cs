using System.Text.Json;
using System.Text.RegularExpressions;
using NetStrip.Core;
using NetStrip.Core.Settings;
using NetStrip.Core.Tests.Fakes;

namespace NetStrip.Core.Tests.Settings;

public class SettingsStoreTests
{
    private const string Path = @"C:\data\settings.json";

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeFileSystem _fs = new();
    private readonly FakeLog _log = new();

    private SettingsStore Create() => new(Path, _fs, _log, _clock);

    [Fact]
    public void Missing_ReturnsDefaults()
    {
        var store = Create();

        var settings = store.Load();

        Assert.Same(settings, store.Current);
        Assert.Equal(1, settings.Version);
        Assert.Equal(SpeedUnits.Bits, settings.Units);
        Assert.Equal("auto", settings.Adapter);
        Assert.Equal(MonitorScope.Primary, settings.Monitors);
        Assert.True(settings.StartWithWindows);
        Assert.True(settings.HideInFullscreen);
        Assert.True(settings.IdleFade);
        Assert.Equal(ThemeSetting.System, settings.Theme);
        Assert.Null(settings.CapGb);
        Assert.Equal(1, settings.CapResetDay);
        Assert.Equal("all", settings.CapAdapter);
        Assert.True(settings.CheckForUpdates);
        Assert.Null(settings.FloatingPosition);
        Assert.Empty(_fs.Moves);
        Assert.Empty(_fs.AtomicWrites);
    }

    [Fact]
    public void Current_BeforeLoad_IsDefaults()
    {
        Assert.Equal(SpeedUnits.Bits, Create().Current.Units);
    }

    [Fact]
    public void Load_ParsesSpecKeys_AsCamelCaseStrings()
    {
        _fs.Files[Path] = """
            { "version": 1, "units": "bytes", "adapter": "11ca5483-84b1-4a07-8773-a98917aee6a8", "monitors": "all",
              "startWithWindows": false, "hideInFullscreen": false, "idleFade": false, "theme": "dark",
              "capGb": 50.5, "capResetDay": 15, "capAdapter": "all", "checkForUpdates": false,
              "floatingPosition": { "x": 120, "y": -40 } }
            """;

        var s = Create().Load();

        Assert.Equal(SpeedUnits.Bytes, s.Units);
        Assert.Equal("11ca5483-84b1-4a07-8773-a98917aee6a8", s.Adapter);
        Assert.Equal(MonitorScope.All, s.Monitors);
        Assert.False(s.StartWithWindows);
        Assert.False(s.HideInFullscreen);
        Assert.False(s.IdleFade);
        Assert.Equal(ThemeSetting.Dark, s.Theme);
        Assert.Equal(50.5, s.CapGb);
        Assert.Equal(15, s.CapResetDay);
        Assert.False(s.CheckForUpdates);
        Assert.Equal(new PixelPoint(120, -40), s.FloatingPosition);
    }

    [Fact]
    public void Save_WritesCamelCaseKeysAndEnumStrings()
    {
        var store = Create();
        store.Load();
        store.Update(s =>
        {
            s.Units = SpeedUnits.Bytes;
            s.Theme = ThemeSetting.Light;
            s.Monitors = MonitorScope.All;
            s.CapGb = 10;
            s.FloatingPosition = new PixelPoint(5, 6);
        });

        var root = JsonDocument.Parse(_fs.Files[Path]).RootElement;
        Assert.Equal("bytes", root.GetProperty("units").GetString());
        Assert.Equal("light", root.GetProperty("theme").GetString());
        Assert.Equal("all", root.GetProperty("monitors").GetString());
        Assert.Equal("auto", root.GetProperty("adapter").GetString());
        Assert.Equal(10, root.GetProperty("capGb").GetDouble());
        Assert.Equal(1, root.GetProperty("capResetDay").GetInt32());
        Assert.Equal(5, root.GetProperty("floatingPosition").GetProperty("x").GetInt32());
        Assert.True(root.GetProperty("startWithWindows").GetBoolean());
        Assert.True(root.TryGetProperty("capGb", out _));
    }

    [Fact]
    public void Roundtrip_PreservesUnknownFields()
    {
        _fs.Files[Path] = """{ "units": "bytes", "futureFlag": true, "futureObject": { "a": [1, 2, 3] }, "futureText": "hi" }""";
        var store = Create();
        store.Load();

        store.Update(s => s.Theme = ThemeSetting.Dark);

        var root = JsonDocument.Parse(_fs.Files[Path]).RootElement;
        Assert.True(root.GetProperty("futureFlag").GetBoolean());
        Assert.Equal(3, root.GetProperty("futureObject").GetProperty("a").GetArrayLength());
        Assert.Equal("hi", root.GetProperty("futureText").GetString());
        Assert.Equal("bytes", root.GetProperty("units").GetString());
        Assert.Equal("dark", root.GetProperty("theme").GetString());

        var reloaded = Create().Load();
        Assert.True(reloaded.Extra!.ContainsKey("futureFlag"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{ "units": "furlongs" }""")]
    [InlineData("""{ "capGb": "lots" }""")]
    public void Invalid_BacksUpAndDefaults(string content)
    {
        _fs.Files[Path] = content;
        var store = Create();

        var s = store.Load();

        var move = Assert.Single(_fs.Moves);
        Assert.Equal(Path, move.From);
        Assert.Equal(Path + ".bad-20261023120000", move.To);
        Assert.Equal(SpeedUnits.Bits, s.Units);
        Assert.Equal("auto", s.Adapter);
        Assert.Same(s, store.Current);
        Assert.NotEmpty(_log.Warnings.Concat(_log.Errors));
    }

    [Fact]
    public void Invalid_WhenBackupMoveFails_StillReturnsDefaults()
    {
        var fs = new ThrowingMoveFileSystem { Files = { [Path] = "{ nope" } };
        var store = new SettingsStore(Path, fs, _log, _clock);

        var s = store.Load();

        Assert.Equal(SpeedUnits.Bits, s.Units);
        Assert.NotEmpty(_log.Errors);
    }

    [Fact]
    public void BrieflyLockedFile_IsReadAfterRetry()
    {
        _fs.Files[Path] = """{ "units": "bytes" }""";
        _fs.FailingReads = 2;

        Assert.Equal(SpeedUnits.Bytes, Create().Load().Units);
        Assert.Empty(_fs.Moves);
    }

    [Fact]
    public void LockedFile_UsesDefaults_WithoutBackingUpOrOverwritingIt()
    {
        var original = """{ "units": "bytes" }""";
        _fs.Files[Path] = original;
        _fs.FailingReads = 100;
        var store = Create();

        var s = store.Load();
        store.Update(x => x.IdleFade = false);

        Assert.Equal(SpeedUnits.Bits, s.Units);
        Assert.False(store.Current.IdleFade);    // the change still applies for this session
        Assert.Empty(_fs.Moves);
        Assert.Empty(_fs.AtomicWrites);
        Assert.Equal(original, _fs.Files[Path]);
        Assert.NotEmpty(_log.Errors);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(28, 28)]
    [InlineData(29, 28)]
    [InlineData(31, 28)]
    public void CapResetDay_ClampedTo1_28(int stored, int expected)
    {
        _fs.Files[Path] = $$"""{ "capResetDay": {{stored}} }""";

        Assert.Equal(expected, Create().Load().CapResetDay);
    }

    [Fact]
    public void CapResetDay_ClampedOnUpdate()
    {
        var store = Create();
        store.Load();

        store.Update(s => s.CapResetDay = 40);

        Assert.Equal(28, store.Current.CapResetDay);
        Assert.Equal(28, JsonDocument.Parse(_fs.Files[Path]).RootElement.GetProperty("capResetDay").GetInt32());
    }

    [Fact]
    public void Update_RaisesChanged_AndSaves()
    {
        var store = Create();
        store.Load();
        var raised = new List<AppSettings>();
        store.Changed += raised.Add;

        store.Update(s => s.Units = SpeedUnits.Bytes);

        var changed = Assert.Single(raised);
        Assert.Same(store.Current, changed);
        Assert.Equal(SpeedUnits.Bytes, changed.Units);
        var write = Assert.Single(_fs.AtomicWrites);
        Assert.Equal(Path, write.Path);
        Assert.Equal("bytes", JsonDocument.Parse(write.Contents).RootElement.GetProperty("units").GetString());
    }

    [Fact]
    public void Update_DoesNotMutateSnapshotsHandedOutEarlier()
    {
        var store = Create();
        var before = store.Load();

        store.Update(s => s.Units = SpeedUnits.Bytes);

        Assert.Equal(SpeedUnits.Bits, before.Units);
        Assert.Equal(SpeedUnits.Bytes, store.Current.Units);
    }

    [Fact]
    public void Update_WhenWriteFails_StillAppliesAndRaises()
    {
        var fs = new ThrowingWriteFileSystem();
        var store = new SettingsStore(Path, fs, _log, _clock);
        store.Load();
        var raised = 0;
        store.Changed += _ => raised++;

        store.Update(s => s.IdleFade = false);

        Assert.False(store.Current.IdleFade);
        Assert.Equal(1, raised);
        Assert.NotEmpty(_log.Errors);
    }

    [Fact]
    public void Constructor_WithoutClock_UsesSystemClockForBackupName()
    {
        _fs.Files[Path] = "{ nope";
        new SettingsStore(Path, _fs, _log).Load();

        Assert.Matches(new Regex(@"\.bad-\d{14}$"), Assert.Single(_fs.Moves).To);
    }

    private sealed class ThrowingMoveFileSystem : IFileSystem
    {
        public Dictionary<string, string> Files { get; } = new();
        public string? ReadAllText(string path) => Files.GetValueOrDefault(path);
        public void WriteAllTextAtomic(string path, string contents) => Files[path] = contents;
        public void Move(string from, string to) => throw new IOException("locked");
    }

    private sealed class ThrowingWriteFileSystem : IFileSystem
    {
        public string? ReadAllText(string path) => null;
        public void WriteAllTextAtomic(string path, string contents) => throw new IOException("disk full");
        public void Move(string from, string to) { }
    }
}
