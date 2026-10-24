using System.Globalization;
using NetStrip.Core.Presentation;

namespace NetStrip.Core.Tests.Presentation;

public class CapStatusTests
{
    private const long Gb = 1L << 30;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0L, 100L, CapLevel.None)]
    [InlineData(79L, 100L, CapLevel.None)]
    [InlineData(80L, 100L, CapLevel.Warning80)]        // exactly 80 % warns
    [InlineData(99L, 100L, CapLevel.Warning80)]
    [InlineData(100L, 100L, CapLevel.Reached100)]      // exactly 100 % is critical
    [InlineData(250L, 100L, CapLevel.Reached100)]
    [InlineData(-5L, 100L, CapLevel.None)]
    public void Level_Thresholds(long used, long cap, CapLevel expected)
    {
        Assert.Equal(expected, CapStatus.Level(used, cap));
    }

    [Fact]
    public void Level_ExactThresholds_LargeCap_NoRoundingError()
    {
        var cap = 50 * Gb;
        Assert.Equal(CapLevel.None, CapStatus.Level(cap * 4 / 5 - 1, cap));
        Assert.Equal(CapLevel.Warning80, CapStatus.Level(cap * 4 / 5, cap));
        Assert.Equal(CapLevel.Warning80, CapStatus.Level(cap - 1, cap));
        Assert.Equal(CapLevel.Reached100, CapStatus.Level(cap, cap));
        Assert.Equal(CapLevel.Reached100, CapStatus.Level(long.MaxValue, long.MaxValue));
        Assert.Equal(CapLevel.Warning80, CapStatus.Level(long.MaxValue - 1, long.MaxValue));
    }

    [Fact]
    public void Level_NoCapOrZeroCap_IsNone()
    {
        Assert.Equal(CapLevel.None, CapStatus.Level(null));
        Assert.Equal(CapLevel.None, CapStatus.Level((500L, 0L)));
        Assert.Equal(CapLevel.None, CapStatus.Level((500L, -1L)));
        Assert.Equal(CapLevel.Reached100, CapStatus.Level((500L, 400L)));
    }

    [Theory]
    [InlineData(0L, 100L, 0)]
    [InlineData(29L, 100L, 29)]                         // 0.29 * 100 is 28.999… in doubles
    [InlineData(624L, 1000L, 62)]
    [InlineData(629L, 1000L, 62)]                       // rounds down
    [InlineData(799L, 1000L, 79)]                       // never shows 80 % while the tint is still normal
    [InlineData(999L, 1000L, 99)]                       // never shows 100 % before the cap is reached
    [InlineData(1000L, 1000L, 100)]
    [InlineData(1500L, 1000L, 150)]                     // over the cap: not clamped
    [InlineData(500L, 0L, 0)]                           // cap of 0 guard
    [InlineData(-5L, 100L, 0)]
    public void Percent_RoundsDown(long used, long cap, int expected)
    {
        Assert.Equal(expected, CapStatus.Percent(used, cap));
    }

    [Fact]
    public void Percent_HugeRatio_Saturates()
    {
        Assert.Equal(int.MaxValue, CapStatus.Percent(long.MaxValue, 1));
    }

    [Fact]
    public void Tooltip_NoCap_IsTodayLineOnly()
    {
        var today = new Totals(UpBytes: 220_200_960, DownBytes: 1_331_439_861);
        Assert.Equal("Today ↓ 1.24 GB ↑ 210 MB", CapStatus.Tooltip(today, null, Inv));
        Assert.Equal("Today ↓ 1.24 GB ↑ 210 MB", CapStatus.Tooltip(today, (5L, 0L), Inv));
    }

    [Fact]
    public void Tooltip_WithCap_AddsThisCycleLine()
    {
        var today = new Totals(UpBytes: 220_200_960, DownBytes: 1_331_439_861);
        var cap = 50 * Gb;
        Assert.Equal("Today ↓ 1.24 GB ↑ 210 MB\nThis cycle: 62% of 50.0 GB",
            CapStatus.Tooltip(today, (cap * 62 / 100 + 1, cap), Inv));
        Assert.Equal("Today ↓ 0 B ↑ 0 B\nThis cycle: 0% of 50.0 GB", CapStatus.Tooltip(default, (0L, cap), Inv));
        Assert.Equal("Today ↓ 0 B ↑ 0 B\nThis cycle: 120% of 50.0 GB", CapStatus.Tooltip(default, (cap * 6 / 5, cap), Inv));
    }

    [Fact]
    public void Tooltip_UsesCulture()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("Today ↓ 1,24 GB ↑ 0 B\nThis cycle: 50% of 2,00 GB",
            CapStatus.Tooltip(new Totals(0, 1_331_439_861), (Gb, 2 * Gb), de));
    }
}
