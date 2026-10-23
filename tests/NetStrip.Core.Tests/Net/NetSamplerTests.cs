using NetStrip.Core;
using NetStrip.Core.Net;
using NetStrip.Core.Tests.Fakes;

namespace NetStrip.Core.Tests.Net;

public class NetSamplerTests
{
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeInterfaceTable _table = new();
    private readonly FakeLog _log = new();

    private NetSampler Create(string setting = "auto") =>
        new(_table, _clock, _log) { AdapterSetting = setting };

    [Fact]
    public void FirstTick_IsZero()
    {
        _table.Set("a", 1_000_000, 500_000);
        _table.BestId = "a";
        var sampler = Create();

        var s = sampler.Tick();

        Assert.Equal(0, s.DownBytesPerSec);
        Assert.Equal(0, s.UpBytesPerSec);
        Assert.Equal("a", s.AdapterId);
    }

    [Fact]
    public void Delta_DividedByElapsed()
    {
        _table.Set("a", 1_000, 100);
        _table.BestId = "a";
        var sampler = Create();
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(2));
        _table.Set("a", 5_000, 1_100);
        var s = sampler.Tick();

        Assert.Equal(2000, s.DownBytesPerSec);
        Assert.Equal(500, s.UpBytesPerSec);
        Assert.Equal(_clock.Now, s.Time);
    }

    [Fact]
    public void CounterDecrease_ReportsZero()
    {
        _table.Set("a", 10_000, 10_000);
        _table.BestId = "a";
        var sampler = Create();
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("a", 500, 10_500); // down counter reset, up still grows
        var s = sampler.Tick();

        Assert.Equal(0, s.DownBytesPerSec);
        Assert.Equal(0, s.UpBytesPerSec); // whole adapter reports 0 for the tick

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("a", 1_500, 11_500);
        s = sampler.Tick();
        Assert.Equal(1000, s.DownBytesPerSec);
        Assert.Equal(1000, s.UpBytesPerSec);
    }

    [Fact]
    public void CounterDecrease_OnlyAffectsThatAdapter_InAllMode()
    {
        _table.Set("a", 10_000, 0);
        _table.Set("b", 1_000, 0);
        var sampler = Create("all");
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("a", 100, 0);     // reset
        _table.Set("b", 3_000, 0);   // +2000
        var s = sampler.Tick();

        Assert.Equal(2000, s.DownBytesPerSec);
    }

    [Fact]
    public void GapOver5s_ResyncsToZero()
    {
        _table.Set("a", 1_000, 1_000);
        _table.BestId = "a";
        var sampler = Create();
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(6));
        _table.Set("a", 601_000, 601_000);
        var s = sampler.Tick();
        Assert.Equal(0, s.DownBytesPerSec);
        Assert.Equal(0, s.UpBytesPerSec);

        // resynced: the next normal tick measures against the counters seen after the gap
        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("a", 602_000, 601_500);
        s = sampler.Tick();
        Assert.Equal(1000, s.DownBytesPerSec);
        Assert.Equal(500, s.UpBytesPerSec);
    }

    [Fact]
    public void Gap_ExactlyFiveSeconds_StillMeasures()
    {
        _table.Set("a", 0, 0);
        _table.BestId = "a";
        var sampler = Create();
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(5));
        _table.Set("a", 5_000, 0);
        Assert.Equal(1000, sampler.Tick().DownBytesPerSec);
    }

    [Fact]
    public void Auto_UsesBestInterface()
    {
        _table.Set("wifi", 0, 0);
        _table.Set("eth", 0, 0);
        _table.BestId = "wifi";
        var sampler = Create();
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("wifi", 3_000, 0);
        _table.Set("eth", 7_000, 0);
        var s = sampler.Tick();

        Assert.Equal("wifi", s.AdapterId);
        Assert.Equal(3000, s.DownBytesPerSec);
        Assert.Equal("wifi", sampler.CurrentAdapter?.Id);
    }

    [Fact]
    public void Auto_BestRouteExcluded_FallsBackToAll()
    {
        _table.Set("vpn", 0, 0, included: false);
        _table.Set("wifi", 0, 0);
        _table.Set("eth", 0, 0);
        _table.BestId = "vpn";
        var sampler = Create();
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("vpn", 90_000, 0, included: false);
        _table.Set("wifi", 1_000, 0);
        _table.Set("eth", 2_000, 0);
        var s = sampler.Tick();

        Assert.Equal("all", s.AdapterId);
        Assert.Equal(3000, s.DownBytesPerSec);
        Assert.Null(sampler.CurrentAdapter);
    }

    [Fact]
    public void Auto_NoBestRoute_FallsBackToAll()
    {
        _table.Set("wifi", 0, 0);
        _table.BestId = null;
        var sampler = Create();
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("wifi", 1_000, 0);
        var s = sampler.Tick();

        Assert.Equal("all", s.AdapterId);
        Assert.Equal(1000, s.DownBytesPerSec);
    }

    [Fact]
    public void Auto_ReevaluatedEveryTenTicks()
    {
        _table.Set("a", 0, 0);
        _table.Set("b", 0, 0);
        _table.BestId = "a";
        var sampler = Create();
        Assert.Equal("a", sampler.Tick().AdapterId);   // tick 1 evaluates
        _table.BestId = "b";
        for (var i = 2; i <= 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("a", sampler.Tick().AdapterId); // ticks 2..10 use the cached route
        }
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("b", sampler.Tick().AdapterId);       // tick 11 re-evaluates
    }

    [Fact]
    public void AdapterDisconnecting_IsReported_WithoutItsOldAddress()
    {
        _table.Set("w", 0, 0, kind: AdapterKind.WiFi, ipv4: "192.168.1.112");
        _table.BestId = "w";
        var sampler = Create();
        sampler.Tick();
        Assert.Equal(new AdapterInfo("w", "Adapter w", AdapterKind.WiFi, "192.168.1.112", Connected: true), sampler.CurrentAdapter);

        // Wi-Fi drops: the table still lists the adapter (and, until refreshed, may still carry the old address).
        _table.Set("w", 0, 0, kind: AdapterKind.WiFi, ipv4: "192.168.1.112", connected: false);
        _clock.Advance(TimeSpan.FromSeconds(1));
        sampler.Tick();

        var adapter = Assert.Single(sampler.Adapters);
        Assert.False(adapter.Connected);
        Assert.Null(adapter.IPv4);

        _table.Set("w", 0, 0, kind: AdapterKind.WiFi, ipv4: "192.168.1.113");
        _clock.Advance(TimeSpan.FromSeconds(1));
        sampler.Tick();
        Assert.Equal(new AdapterInfo("w", "Adapter w", AdapterKind.WiFi, "192.168.1.113", Connected: true), Assert.Single(sampler.Adapters));
    }

    [Fact]
    public void AdapterChange_ReevaluatesBestRouteOnTheNextTick()
    {
        _table.Set("a", 0, 0);
        _table.Set("b", 0, 0);
        _table.BestId = "a";
        var sampler = Create();
        Assert.Equal("a", sampler.Tick().AdapterId);

        // "a" disconnects and the route moves to "b": no waiting for the 10-tick re-evaluation.
        _table.Set("a", 0, 0, connected: false);
        _table.BestId = "b";
        _clock.Advance(TimeSpan.FromSeconds(1));
        sampler.Tick();                                  // sees the change
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("b", sampler.Tick().AdapterId);     // re-evaluated
    }

    [Fact]
    public void All_SumsIncludedOnly()
    {
        _table.Set("a", 0, 0);
        _table.Set("b", 0, 0);
        _table.Set("loop", 0, 0, included: false);
        var sampler = Create("all");
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("a", 1_000, 10);
        _table.Set("b", 2_000, 20);
        _table.Set("loop", 999_999, 999_999, included: false);
        var s = sampler.Tick();

        Assert.Equal("all", s.AdapterId);
        Assert.Equal(3000, s.DownBytesPerSec);
        Assert.Equal(30, s.UpBytesPerSec);
        Assert.Null(sampler.CurrentAdapter);
    }

    [Fact]
    public void SpecificAdapter_Missing_ReportsZeroAndNullCurrent()
    {
        _table.Set("a", 0, 0);
        var sampler = Create("gone");
        var s = sampler.Tick();

        Assert.Equal("gone", s.AdapterId);
        Assert.Equal(0, s.DownBytesPerSec);
        Assert.Equal(0, s.UpBytesPerSec);
        Assert.Null(sampler.CurrentAdapter);
    }

    [Fact]
    public void SpecificAdapter_Returns_Resumes()
    {
        _table.Set("a", 0, 0);
        _table.Set("b", 0, 0);
        var sampler = Create("b");
        sampler.Tick();
        Assert.Equal("b", sampler.CurrentAdapter?.Id);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Remove("b");
        var s = sampler.Tick();
        Assert.Equal("b", s.AdapterId);
        Assert.Equal(0, s.DownBytesPerSec);
        Assert.Null(sampler.CurrentAdapter);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("b", 50_000, 0); // back, counter unrelated to the old value
        s = sampler.Tick();
        Assert.Equal(0, s.DownBytesPerSec); // first sighting after return: no baseline, no spike
        Assert.Equal("b", sampler.CurrentAdapter?.Id);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("b", 51_000, 0);
        s = sampler.Tick();
        Assert.Equal(1000, s.DownBytesPerSec);
        Assert.Equal("b", s.AdapterId);
    }

    [Fact]
    public void SwitchingAdapter_DoesNotSpike()
    {
        _table.Set("a", 0, 0);
        _table.Set("b", 1_000_000, 0);
        var sampler = Create("a");
        sampler.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("b", 1_000_500, 0);
        sampler.AdapterSetting = "b";
        var s = sampler.Tick();

        Assert.Equal("b", s.AdapterId);
        Assert.Equal(500, s.DownBytesPerSec);
    }

    [Fact]
    public void Adapters_ListsIncludedOnly()
    {
        _table.Set("a", 0, 0, kind: AdapterKind.WiFi, ipv4: "10.0.0.2");
        _table.Set("loop", 0, 0, included: false);
        var sampler = Create();
        Assert.Empty(sampler.Adapters);

        sampler.Tick();

        var adapter = Assert.Single(sampler.Adapters);
        Assert.Equal(new AdapterInfo("a", "Adapter a", AdapterKind.WiFi, "10.0.0.2"), adapter);
    }

    [Fact]
    public void TickAndPublish_RaisesEverySample()
    {
        // Pausing is a display concern (AppHost); the sampler must keep publishing so usage keeps counting.
        _table.Set("a", 0, 0);
        _table.BestId = "a";
        var sampler = Create();
        var raised = new List<Sample>();
        sampler.SampleProduced += raised.Add;

        var first = sampler.TickAndPublish();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _table.Set("a", 1_000, 0);
        var second = sampler.TickAndPublish();

        Assert.Equal([first, second], raised);
        Assert.Equal(1000, second.DownBytesPerSec);
    }

    [Fact]
    public async Task Start_PublishesSamples_AndDisposeStopsThem()
    {
        _table.Set("a", 0, 0);
        _table.BestId = "a";
        var sampler = Create();
        var got = new TaskCompletionSource<Sample>(TaskCreationOptions.RunContinuationsAsynchronously);
        sampler.SampleProduced += s => got.TrySetResult(s);

        sampler.Start();
        await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sampler.Dispose();
    }
}

public class AdapterSelectionTests
{
    private static InterfaceCounters C(string id, bool included = true) =>
        new(id, id, AdapterKind.Ethernet, null, 0, 0, included);

    [Fact]
    public void All_ReturnsIncluded()
    {
        var (chosen, id) = AdapterSelection.Resolve([C("a"), C("b", false), C("c")], "all", "a");
        Assert.Equal(["a", "c"], chosen.Select(c => c.Id));
        Assert.Equal("all", id);
    }

    [Fact]
    public void Auto_NullBest_IsAll()
    {
        var (chosen, id) = AdapterSelection.Resolve([C("a"), C("b")], "auto", null);
        Assert.Equal(2, chosen.Count);
        Assert.Equal("all", id);
    }

    [Fact]
    public void Specific_IsCaseInsensitive()
    {
        var (chosen, id) = AdapterSelection.Resolve([C("AB-1")], "ab-1", null);
        Assert.Single(chosen);
        Assert.Equal("ab-1", id);
    }

    [Fact]
    public void Specific_Excluded_TreatedAsMissing()
    {
        var (chosen, id) = AdapterSelection.Resolve([C("a", false)], "a", null);
        Assert.Empty(chosen);
        Assert.Equal("a", id);
    }
}
