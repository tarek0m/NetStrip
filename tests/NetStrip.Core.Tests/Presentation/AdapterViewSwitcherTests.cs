using NetStrip.Core.Presentation;

namespace NetStrip.Core.Tests.Presentation;

public class AdapterViewSwitcherTests
{
    private static readonly AdapterInfo Wifi = new("wifi", "Wi-Fi", AdapterKind.WiFi, "192.168.1.5");
    private static readonly AdapterInfo Eth = new("eth", "Ethernet", AdapterKind.Ethernet, "10.0.0.2");
    private static readonly AdapterInfo Usb = new("usb", "USB Ethernet", AdapterKind.Ethernet, null, Connected: false);
    private static readonly AdapterInfo Cell = new("cell", "Cellular", AdapterKind.Cellular, "100.64.0.9");

    private static AdapterViewSwitcher Build(string setting, string? displayed, params AdapterInfo[] adapters)
    {
        var s = new AdapterViewSwitcher();
        s.Update(setting, displayed, adapters);
        return s;
    }

    /// <summary>Views as short labels: "configured", "all" or the adapter id.</summary>
    private static string[] Labels(AdapterViewSwitcher s) =>
        s.Views.Select(v => v.Kind switch
        {
            AdapterViewKind.Configured => "configured",
            AdapterViewKind.AllAdapters => "all",
            _ => v.Adapter!.Id,
        }).ToArray();

    private static Sample S(long up, long down, string adapterId = "wifi", params AdapterTraffic[] traffic) =>
        new(DateTimeOffset.UnixEpoch, adapterId, up, down, traffic.Length == 0 ? null : traffic);

    private static AdapterTraffic T(string id, long up, long down) => new(id, up, down, up, down);

    [Fact]
    public void Views_ConfiguredThenAllThenConnectedThenDisconnected_InSourceOrder()
    {
        var s = Build("eth", "eth", Usb, Wifi, Eth, Cell);
        Assert.Equal(["configured", "all", "wifi", "cell", "usb"], Labels(s));
        Assert.Same(AdapterView.Configured, s.Current);
        Assert.True(s.CanSwitch);
    }

    [Fact]
    public void Views_AllSetting_HasNoSeparateAllView()
    {
        var s = Build(AdapterIds.All, null, Wifi, Usb, Eth);
        Assert.Equal(["configured", "wifi", "eth", "usb"], Labels(s));
    }

    [Fact]
    public void Views_Auto_SkipsTheBestRouteAdapter()
    {
        var s = Build(AdapterIds.Auto, "wifi", Wifi, Eth);
        Assert.Equal(["configured", "all", "eth"], Labels(s));
    }

    [Fact]
    public void Views_AutoWithoutRoute_ShowsAllSoNoSeparateAllView()
    {
        var s = Build(AdapterIds.Auto, null, Wifi, Eth);
        Assert.Equal(["configured", "wifi", "eth"], Labels(s));
    }

    [Fact]
    public void Views_ConfiguredAdapterMatchesIgnoringCase()
    {
        var s = Build("WIFI", null, Wifi, Eth);
        Assert.Equal(["configured", "all", "eth"], Labels(s));
    }

    [Fact]
    public void Views_MissingConfiguredAdapter_ListsTheOthers()
    {
        var s = Build("gone", null, Wifi);
        Assert.Equal(["configured", "wifi"], Labels(s));
    }

    [Theory]
    [InlineData(AdapterIds.Auto, "wifi")]
    [InlineData(AdapterIds.Auto, null)]
    [InlineData(AdapterIds.All, null)]
    [InlineData("wifi", "wifi")]
    public void Views_SingleAdapter_IsOneView(string setting, string? displayed)
    {
        var s = Build(setting, displayed, Wifi);
        Assert.Equal(["configured"], Labels(s));
        Assert.False(s.CanSwitch);
        Assert.False(s.Next());
        Assert.Equal(0, s.Index);
    }

    [Fact]
    public void Views_NoAdapters_IsOneView()
    {
        Assert.Equal(["configured"], Labels(Build(AdapterIds.Auto, null)));
        Assert.Equal(["configured"], Labels(new AdapterViewSwitcher()));
    }

    [Fact]
    public void NextAndPrevious_WrapAround()
    {
        var s = Build("eth", "eth", Wifi, Eth);   // configured, all, wifi
        Assert.True(s.Previous());
        Assert.Equal("wifi", s.Current.Adapter!.Id);
        Assert.True(s.Next());
        Assert.Same(AdapterView.Configured, s.Current);
        Assert.True(s.Next());
        Assert.Same(AdapterView.AllAdapters, s.Current);
        s.Next();
        s.Next();
        Assert.Equal(0, s.Index);
    }

    [Fact]
    public void Reset_ReturnsToConfigured()
    {
        var s = Build("eth", "eth", Wifi, Eth);
        s.Next();
        s.Next();
        s.Reset();
        Assert.Equal(0, s.Index);
        Assert.Same(AdapterView.Configured, s.Current);
    }

    [Fact]
    public void Update_KeepsSelectedAdapterById_AcrossChanges()
    {
        var s = Build("eth", "eth", Wifi, Eth, Cell);   // configured, all, wifi, cell
        s.Previous();
        Assert.Equal("cell", s.Current.Adapter!.Id);

        // Wi-Fi drops (moves behind the connected ones) and the cellular address changes.
        var cell2 = Cell with { IPv4 = "100.64.0.10" };
        s.Update("eth", "eth", [Wifi with { Connected = false, IPv4 = null }, Eth, cell2]);
        Assert.Equal(["configured", "all", "cell", "wifi"], Labels(s));
        Assert.Equal(2, s.Index);
        Assert.Equal("100.64.0.10", s.Current.Adapter!.IPv4);
    }

    [Fact]
    public void Update_FallsBackToConfigured_WhenSelectedAdapterDisappears()
    {
        var s = Build("eth", "eth", Wifi, Eth, Cell);
        s.Previous();   // cell
        s.Update("eth", "eth", [Wifi, Eth]);
        Assert.Equal(0, s.Index);
        Assert.Same(AdapterView.Configured, s.Current);
    }

    [Fact]
    public void Update_FallsBackToConfigured_WhenAutoRouteMovesToTheSelectedAdapter()
    {
        var s = Build(AdapterIds.Auto, "wifi", Wifi, Eth);   // configured, all, eth
        s.Previous();
        Assert.Equal("eth", s.Current.Adapter!.Id);
        s.Update(AdapterIds.Auto, "eth", [Wifi, Eth]);
        Assert.Same(AdapterView.Configured, s.Current);
    }

    [Fact]
    public void Update_KeepsAllAdaptersSelection()
    {
        var s = Build("eth", "eth", Wifi, Eth);
        s.Next();
        s.Update("eth", "eth", [Wifi, Eth, Cell]);
        Assert.Same(AdapterView.AllAdapters, s.Current);
    }

    [Fact]
    public void Update_UnchangedInputs_KeepsTheSameViews()
    {
        IReadOnlyList<AdapterInfo> adapters = [Wifi, Eth];
        var s = new AdapterViewSwitcher();
        s.Update(AdapterIds.Auto, "wifi", adapters);
        var views = s.Views;
        s.Update(AdapterIds.Auto, "wifi", adapters);
        Assert.Same(views, s.Views);
    }

    [Fact]
    public void RatesFor_Configured_IsTheSamplesOwn()
    {
        var sample = S(10, 20, "wifi", T("wifi", 1, 2), T("eth", 3, 4));
        Assert.Equal((10L, 20L), AdapterView.Configured.RatesFor(sample));
    }

    [Fact]
    public void RatesFor_AllAdapters_SumsTraffic()
    {
        var sample = S(10, 20, "wifi", T("wifi", 1, 2), T("eth", 3, 4));
        Assert.Equal((4L, 6L), AdapterView.AllAdapters.RatesFor(sample));
    }

    [Fact]
    public void RatesFor_Adapter_PicksItsEntry_OrZero()
    {
        var sample = S(10, 20, "wifi", T("wifi", 1, 2), T("ETH", 3, 4));
        Assert.Equal((3L, 4L), AdapterView.ForAdapter(Eth).RatesFor(sample));
        Assert.Equal((0L, 0L), AdapterView.ForAdapter(Cell).RatesFor(sample));
    }

    [Fact]
    public void RatesFor_SampleWithoutTraffic_CountsAsItsDisplayedAdapter()
    {
        var sample = S(10, 20, "wifi");
        Assert.Equal((10L, 20L), AdapterView.AllAdapters.RatesFor(sample));
        Assert.Equal((10L, 20L), AdapterView.ForAdapter(Wifi).RatesFor(sample));
        Assert.Equal((0L, 0L), AdapterView.ForAdapter(Eth).RatesFor(sample));
    }

    [Fact]
    public void UsageScope_IsAdapterId_OrAll()
    {
        Assert.Equal(AdapterIds.All, AdapterView.Configured.UsageScope);
        Assert.Equal(AdapterIds.All, AdapterView.AllAdapters.UsageScope);
        Assert.Equal("eth", AdapterView.ForAdapter(Eth).UsageScope);
    }

    [Fact]
    public void PeakRate_ProjectsTheView_WithTheGraphFloor()
    {
        var g = new GraphBuffer();
        g.Push(S(500, 9000, "wifi", T("wifi", 500, 9000), T("eth", 100, 200)));
        g.Push(S(700, 300, "wifi", T("wifi", 700, 300), T("eth", 5000, 50)));

        Assert.Equal(g.Max, AdapterView.Configured.PeakRate(g.Items));
        Assert.Equal(5000, AdapterView.ForAdapter(Eth).PeakRate(g.Items));
        Assert.Equal(9200, AdapterView.AllAdapters.PeakRate(g.Items));
        Assert.Equal(GraphBuffer.MaxFloor, AdapterView.ForAdapter(Cell).PeakRate(g.Items));
    }
}
