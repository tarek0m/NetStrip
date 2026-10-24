using System.Text.Json;
using NetStrip.Core;
using NetStrip.Core.Tests.Fakes;
using NetStrip.Core.Usage;

namespace NetStrip.Core.Tests.Usage;

public class UsageStoreTests
{
    private const string Path = @"C:\data\usage.json";
    private const long Gb = 1024L * 1024 * 1024;

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeFileSystem _fs = new();
    private readonly FakeLog _log = new();

    private UsageStore Create() => new(Path, _clock, _fs, _log);

    private static Sample S(long up, long down, string adapter = "a") =>
        new(DateTimeOffset.UnixEpoch, adapter, up, down);

    private void At(int y, int m, int d, int h = 12, int min = 0, int s = 0) =>
        _clock.Now = new DateTimeOffset(y, m, d, h, min, s, TimeSpan.Zero);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private string SavedJson()
    {
        Assert.True(_fs.Files.TryGetValue(Path, out var json));
        return json!;
    }

    [Fact]
    public void Add_AccumulatesToday()
    {
        var store = Create();
        store.Add(S(100, 1000));
        store.Add(S(50, 500));
        store.Add(S(1, 2, "b"));

        Assert.Equal(new Totals(150, 1500), store.Today("a"));
        Assert.Equal(new Totals(1, 2), store.Today("b"));
        Assert.Equal(new Totals(151, 1502), store.Today("all"));
        Assert.Equal(new Totals(0, 0), store.Today("missing"));
    }

    [Fact]
    public void Today_All_IncludesSamplesRecordedUnderAllKey()
    {
        var store = Create();
        store.Add(S(10, 20, "all"));
        store.Add(S(1, 2, "a"));

        Assert.Equal(new Totals(11, 22), store.Today("all"));
        Assert.Equal(new Totals(1, 2), store.Today("a"));
    }

    [Fact]
    public void Add_WithTraffic_RecordsEachAdapterInExactBytes()
    {
        var store = Create();

        // Displayed as "all" at some rate; usage comes from the per-adapter bytes, not the displayed rate.
        store.Add(new Sample(DateTimeOffset.UnixEpoch, "all", 999_999, 999_999,
            [new AdapterTraffic("wifi", 10, 20, 5, 10), new AdapterTraffic("eth", 1, 2, 1, 2)]));

        Assert.Equal(new Totals(10, 20), store.Today("wifi"));
        Assert.Equal(new Totals(1, 2), store.Today("eth"));
        Assert.Equal(new Totals(11, 22), store.Today("all"));
    }

    [Fact]
    public void Add_WithIdleTraffic_ChangesNothing()
    {
        var store = Create();

        store.Add(new Sample(DateTimeOffset.UnixEpoch, "a", 0, 0, [new AdapterTraffic("a", 0, 0, 0, 0)]));
        store.Save();

        Assert.Empty(_fs.AtomicWrites);
    }

    [Fact]
    public void Cap_CountsTrafficFromEveryAdapter()
    {
        var store = Create();
        store.CapGb = 1;
        var fired = new List<CapLevel>();
        store.CapThresholdCrossed += fired.Add;

        store.Add(new Sample(DateTimeOffset.UnixEpoch, "a", 0, 0,
            [new AdapterTraffic("a", 0, Gb / 2, 0, 0), new AdapterTraffic("b", 0, Gb / 3, 0, 0)]));   // 83 % in total

        Assert.Equal([CapLevel.Warning80], fired);
    }

    [Fact]
    public void Midnight_StartsNewBucket()
    {
        var store = Create();
        At(2026, 10, 23, 23, 59, 59);
        store.Add(S(10, 100));
        At(2026, 10, 24, 0, 0, 0);
        store.Add(S(5, 50));

        Assert.Equal(new Totals(5, 50), store.Today("a"));
        At(2026, 10, 23);
        Assert.Equal(new Totals(10, 100), store.Today("a"));
    }

    [Fact]
    public void DstSpringForward_SingleBucketPerDate()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        void AtUtc(int d, int h, int min) =>
            _clock.Now = TimeZoneInfo.ConvertTime(new DateTimeOffset(2027, 3, d, h, min, 0, TimeSpan.Zero), berlin);

        var store = Create();
        AtUtc(27, 23, 30);   // Mar 28 00:30 CET
        store.Add(S(1, 1));
        AtUtc(28, 0, 59);    // Mar 28 01:59 CET
        store.Add(S(1, 1));
        AtUtc(28, 1, 0);     // Mar 28 03:00 CEST (02:00-03:00 does not exist)
        store.Add(S(1, 1));
        AtUtc(28, 21, 59);   // Mar 28 23:59 CEST
        store.Add(S(1, 1));
        AtUtc(28, 22, 0);    // Mar 29 00:00 CEST
        store.Add(S(7, 7));
        store.Save();

        var buckets = Parse(SavedJson()).GetProperty("adapters").GetProperty("a");
        Assert.Equal(["2027-03-28", "2027-03-29"], buckets.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(4, buckets.GetProperty("2027-03-28")[0].GetInt64());
        Assert.Equal(7, buckets.GetProperty("2027-03-29")[0].GetInt64());
    }

    [Fact]
    public void ClockMovesBackADay_AddsToEarlierBucket()
    {
        var store = Create();
        At(2026, 10, 23);
        store.Add(S(10, 100));
        At(2026, 10, 22);
        store.Add(S(3, 30));

        Assert.Equal(new Totals(3, 30), store.Today("a"));     // Oct 22 holds only the new sample
        At(2026, 10, 23);
        Assert.Equal(new Totals(10, 100), store.Today("a"));   // Oct 23 untouched
    }

    [Fact]
    public void Period_ResetDay15()
    {
        var store = Create();
        store.CapResetDay = 15;
        foreach (var (m, d) in new[] { (10, 14), (10, 15), (10, 20), (11, 14), (11, 15) })
        {
            At(2026, m, d);
            store.Add(S(1000, 0));
        }

        At(2026, 10, 20);
        Assert.Equal(new Totals(3000, 0), store.ThisPeriod("a"));   // Oct 15 .. Nov 14
    }

    [Fact]
    public void Period_ResetDay15_EarlyInMonth_StartsPreviousMonth()
    {
        var store = Create();
        store.CapResetDay = 15;
        foreach (var (y, m, d) in new[] { (2026, 12, 14), (2026, 12, 15), (2027, 1, 14), (2027, 1, 15) })
        {
            At(y, m, d);
            store.Add(S(1000, 0));
        }

        At(2027, 1, 5);
        Assert.Equal(new Totals(2000, 0), store.ThisPeriod("a"));   // Dec 15 .. Jan 14
    }

    [Fact]
    public void Period_ResetDay28_February()
    {
        var store = Create();
        store.CapResetDay = 28;
        foreach (var (m, d) in new[] { (2, 27), (2, 28), (3, 1), (3, 27), (3, 28) })
        {
            At(2027, m, d);
            store.Add(S(1000, 0));
        }

        At(2027, 3, 3);
        Assert.Equal(new Totals(3000, 0), store.ThisPeriod("a"));   // Feb 28 .. Mar 27
    }

    [Fact]
    public void CapResetDay_IsClampedTo1_28()
    {
        var store = Create();
        store.CapResetDay = 31;
        Assert.Equal(28, store.CapResetDay);
        store.CapResetDay = 0;
        Assert.Equal(1, store.CapResetDay);
    }

    [Fact]
    public void CapProgress_NullWhenNoCap_ElseUsedAndCapBytes()
    {
        var store = Create();
        store.Add(S(100, 200));
        Assert.Null(store.CapProgress());

        store.CapGb = 2;
        Assert.Equal((300L, 2 * Gb), store.CapProgress());

        store.CapScope = "b";
        Assert.Equal((0L, 2 * Gb), store.CapProgress());
    }

    [Fact]
    public void Cap_FiresWarningOnceThenReached()
    {
        var store = Create();
        store.CapGb = 1;
        var fired = new List<CapLevel>();
        store.CapThresholdCrossed += fired.Add;

        store.Add(S(0, Gb / 2));
        Assert.Empty(fired);

        store.Add(S(0, Gb / 3));                    // 83 %
        Assert.Equal([CapLevel.Warning80], fired);

        store.Add(S(0, 1000));                      // still warning band: no repeat
        Assert.Equal([CapLevel.Warning80], fired);

        store.Add(S(0, Gb / 3));                    // > 100 %
        Assert.Equal([CapLevel.Warning80, CapLevel.Reached100], fired);

        store.Add(S(0, Gb));                        // no repeats
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void Cap_JumpingPast100_FiresReachedOnly_ThenNoWarning()
    {
        var store = Create();
        store.CapGb = 1;
        var fired = new List<CapLevel>();
        store.CapThresholdCrossed += fired.Add;

        store.Add(S(0, Gb + 1));
        store.Add(S(0, 1));

        Assert.Equal([CapLevel.Reached100], fired);
    }

    [Fact]
    public void Cap_OnlyCountsConfiguredScope()
    {
        var store = Create();
        store.CapGb = 1;
        store.CapScope = "wifi";
        var fired = new List<CapLevel>();
        store.CapThresholdCrossed += fired.Add;

        store.Add(S(0, 2 * Gb, "eth"));
        Assert.Empty(fired);

        store.Add(S(0, Gb, "wifi"));
        Assert.Equal([CapLevel.Reached100], fired);
    }

    [Fact]
    public void Cap_FiresAgainInNextPeriod()
    {
        var store = Create();
        store.CapGb = 1;
        var fired = new List<CapLevel>();
        store.CapThresholdCrossed += fired.Add;

        At(2026, 10, 23);
        store.Add(S(0, Gb));
        At(2026, 11, 2);
        store.Add(S(0, Gb));

        Assert.Equal([CapLevel.Reached100, CapLevel.Reached100], fired);
    }

    [Fact]
    public void Cap_FiredStateSurvivesReload()
    {
        var first = Create();
        first.CapGb = 1;
        first.Add(S(0, Gb * 9 / 10));
        first.Save();

        var second = Create();
        second.Load();
        second.CapGb = 1;
        var fired = new List<CapLevel>();
        second.CapThresholdCrossed += fired.Add;

        second.Add(S(0, 1000));                      // warning already fired this period
        Assert.Empty(fired);

        second.Add(S(0, Gb / 5));                    // crosses 100 %
        Assert.Equal([CapLevel.Reached100], fired);

        Assert.Equal(["Warning80", "Reached100"],
            Parse(SavedJsonAfter(second)).GetProperty("capFired").GetProperty("2026-10-01").EnumerateArray().Select(e => e.GetString()!));
    }

    private string SavedJsonAfter(UsageStore store)
    {
        store.Save();
        return SavedJson();
    }

    [Fact]
    public void Save_Load_RoundtripsTotals()
    {
        var first = Create();
        first.Add(S(100, 1000));
        first.Add(S(5, 6, "all"));
        first.Save();

        var second = Create();
        second.Load();

        Assert.Equal(new Totals(100, 1000), second.Today("a"));
        Assert.Equal(new Totals(105, 1006), second.Today("all"));
        Assert.Null(second.LoadNotice);
    }

    [Fact]
    public void Save_JsonShape_MatchesSpec()
    {
        var store = Create();
        store.Add(S(100, 1000));
        store.Save();

        var root = Parse(SavedJson());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var pair = root.GetProperty("adapters").GetProperty("a").GetProperty("2026-10-23");
        Assert.Equal(100, pair[0].GetInt64());
        Assert.Equal(1000, pair[1].GetInt64());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("capFired").ValueKind);
    }

    [Fact]
    public void Save_UsesAtomicWrite()
    {
        var store = Create();
        store.Add(S(1, 1));
        store.Save();

        var write = Assert.Single(_fs.AtomicWrites);
        Assert.Equal(Path, write.Path);
        Assert.Equal(1, Parse(write.Contents).GetProperty("version").GetInt32());
    }

    [Fact]
    public void Save_WithoutChanges_DoesNotRewrite()
    {
        var store = Create();
        store.Add(S(1, 1));
        store.Save();
        store.Save();

        Assert.Single(_fs.AtomicWrites);
    }

    /// <summary>A usage file with one bucket per day for adapter "a", plus any extra raw JSON members.</summary>
    private static string DailyFile(DateOnly first, DateOnly last, string extraAdapters = "", string capFired = "{}")
    {
        var days = new List<string>();
        for (var d = first; d <= last; d = d.AddDays(1)) days.Add($"\"{d:yyyy-MM-dd}\": [1,1]");
        return $$"""{ "version": 1, "adapters": { "a": { {{string.Join(", ", days)}} }{{extraAdapters}} }, "capFired": {{capFired}} }""";
    }

    private List<string> SavedDates(string adapter) =>
        Parse(SavedJson()).GetProperty("adapters").GetProperty(adapter).EnumerateObject().Select(p => p.Name).Order().ToList();

    [Fact]
    public void Retention_Prunes13Months()
    {
        // Today 2026-10-23: the 13-month cutoff is 2025-09-23, and the 397th most recent date is 2025-09-21.
        _fs.Files[Path] = DailyFile(new DateOnly(2025, 7, 1), new DateOnly(2026, 10, 22),
            extraAdapters: """, "old": { "2024-01-01": [9,9] }""",
            capFired: """{ "2025-09-01": ["Warning80"], "2026-10-01": ["Warning80"] }""");
        var store = Create();
        store.Load();
        store.Save();

        var dates = SavedDates("a");
        Assert.Equal(397, dates.Count);
        Assert.Equal("2025-09-21", dates[0]);
        var root = Parse(SavedJson());
        Assert.False(root.GetProperty("adapters").TryGetProperty("old", out _));
        Assert.Equal(["2026-10-01"], root.GetProperty("capFired").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Retention_FewerDatesThanThirteenMonthsHold_KeepsEverything()
    {
        // Sporadic use: data older than 13 months stays until enough newer days of use exist.
        _fs.Files[Path] = """{ "version": 1, "adapters": { "a": { "2024-01-01": [1,1], "2026-10-01": [2,2] } } }""";
        var store = Create();
        store.Load();
        store.Add(S(1, 1));
        store.Save();

        Assert.Equal(["2024-01-01", "2026-10-01", "2026-10-23"], SavedDates("a"));
    }

    [Fact]
    public void ClockJumpingForward_DoesNotDeleteHistory()
    {
        _fs.Files[Path] = DailyFile(new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 22));
        var store = Create();
        store.Load();

        At(2029, 6, 1);                          // e.g. a wrong BIOS date
        store.Add(S(5, 5));
        store.Save();

        var dates = SavedDates("a");
        Assert.Equal(296, dates.Count);          // 295 real days + the bogus one
        Assert.Equal("2026-01-01", dates[0]);
        Assert.Equal("2029-06-01", dates[^1]);
    }

    [Fact]
    public void CapHandlerThrowing_DoesNotEscapeAdd_AndCountingContinues()
    {
        var store = Create();
        store.CapGb = 1;
        store.CapThresholdCrossed += _ => throw new InvalidOperationException("toast failed");

        store.Add(S(0, Gb));                     // crosses both levels; the handler throws
        store.Add(S(0, 1000));

        Assert.Equal(new Totals(0, Gb + 1000), store.Today("a"));
        Assert.NotEmpty(_log.Errors);
    }

    [Fact]
    public void BrieflyLockedFile_IsReadAfterRetry()
    {
        _fs.Files[Path] = """{ "version": 1, "adapters": { "a": { "2026-10-23": [3,4] } } }""";
        _fs.FailingReads = 1;
        var store = Create();

        store.Load();

        Assert.Equal(new Totals(3, 4), store.Today("a"));
        Assert.Empty(_fs.Moves);
    }

    [Fact]
    public void LockedFile_IsNotQuarantined_NotOverwritten_ThenMergedWhenReadable()
    {
        var original = """{ "version": 1, "adapters": { "a": { "2026-10-22": [10,20], "2026-10-23": [1,2] } } }""";
        _fs.Files[Path] = original;
        _fs.FailingReads = 100;
        var store = Create();

        store.Load();
        Assert.Empty(_fs.Moves);
        Assert.Null(store.LoadNotice);

        store.Add(S(5, 5));
        store.Save();                            // still locked: nothing is written over it
        Assert.Empty(_fs.AtomicWrites);
        Assert.Equal(original, _fs.Files[Path]);

        _fs.FailingReads = 0;
        store.Save();                            // readable now: file and session merged, then saved

        var a = Parse(SavedJson()).GetProperty("adapters").GetProperty("a");
        Assert.Equal("[10,20]", a.GetProperty("2026-10-22").GetRawText());
        Assert.Equal("[6,7]", a.GetProperty("2026-10-23").GetRawText());
        Assert.Equal(new Totals(6, 7), store.Today("a"));
    }

    [Fact]
    public void LockedFile_CorruptOnceReadable_IsQuarantinedThenSessionSaved()
    {
        _fs.Files[Path] = "{ not json";
        _fs.FailingReads = 100;
        var store = Create();
        store.Load();
        store.Add(S(5, 5));

        _fs.FailingReads = 0;
        store.Save();

        Assert.Single(_fs.Moves);
        Assert.Equal(["2026-10-23"], SavedDates("a"));
    }

    [Fact]
    public void CorruptFile_RenamedToBad_StartsEmpty_SetsNotice()
    {
        _fs.Files[Path] = "{ not json";
        var store = Create();

        store.Load();

        var move = Assert.Single(_fs.Moves);
        Assert.Equal(Path, move.From);
        Assert.Equal(Path + ".bad-20261023120000", move.To);
        Assert.False(_fs.Files.ContainsKey(Path));
        Assert.NotNull(store.LoadNotice);
        Assert.Equal(new Totals(0, 0), store.Today("all"));
        Assert.NotEmpty(_log.Warnings.Concat(_log.Errors));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"version":1,"adapters":{"a":{"not-a-date":[1,1]}}}""")]
    [InlineData("""{"version":1,"adapters":{"a":{"2026-10-23":[1]}}}""")]
    public void WrongShape_IsTreatedAsCorrupt(string json)
    {
        _fs.Files[Path] = json;
        var store = Create();

        store.Load();

        Assert.Single(_fs.Moves);
        Assert.NotNull(store.LoadNotice);
    }

    [Fact]
    public void MissingFile_StartsEmpty_NoNotice()
    {
        var store = Create();
        store.Load();

        Assert.Empty(_fs.Moves);
        Assert.Null(store.LoadNotice);
        Assert.Equal(new Totals(0, 0), store.ThisPeriod("all"));
    }
}
