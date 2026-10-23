using System.Globalization;
using NetStrip.Core.Formatting;

namespace NetStrip.Core.Tests.Formatting;

public class SpeedFormatterTests
{
    [Theory]
    [InlineData(0L, SpeedUnits.Bytes, "0 KB/s")]
    [InlineData(500L, SpeedUnits.Bytes, "0 KB/s")]
    [InlineData(1024L, SpeedUnits.Bytes, "1.00 KB/s")]
    [InlineData(8_808_038L, SpeedUnits.Bytes, "8.40 MB/s")]
    [InlineData(1_023_000L, SpeedUnits.Bytes, "999 KB/s")]
    [InlineData(1_048_000L, SpeedUnits.Bytes, "1.00 MB/s")] // >= 1000 KB rolls up
    [InlineData(1_250_000L, SpeedUnits.Bits, "10.0 Mb/s")]
    [InlineData(100L, SpeedUnits.Bits, "0 Kb/s")]
    [InlineData(125L, SpeedUnits.Bits, "1.00 Kb/s")]
    [InlineData(-5L, SpeedUnits.Bytes, "0 KB/s")]
    public void FormatSpeed_Invariant(long bytesPerSec, SpeedUnits units, string expected)
    {
        Assert.Equal(expected, SpeedFormatter.FormatSpeed(bytesPerSec, units, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TenGigabit_FitsWidth()
    {
        Assert.Equal("1.16 GB/s", SpeedFormatter.FormatSpeed(1_250_000_000, SpeedUnits.Bytes, CultureInfo.InvariantCulture));

        foreach (var units in new[] { SpeedUnits.Bytes, SpeedUnits.Bits })
        {
            for (double v = 1; v <= 1L << 40; v *= 1.003)
            {
                var text = SpeedFormatter.FormatSpeed((long)v, units, CultureInfo.InvariantCulture);
                Assert.True(text.Length <= 9, $"{v} {units} -> '{text}'");
            }
            var max = SpeedFormatter.FormatSpeed(1L << 40, units, CultureInfo.InvariantCulture);
            Assert.True(max.Length <= 9, $"2^40 {units} -> '{max}'");
        }
    }

    [Fact]
    public void GermanCulture_UsesCommaSeparator()
    {
        Assert.Equal("8,40 MB/s", SpeedFormatter.FormatSpeed(8_808_038, SpeedUnits.Bytes, new CultureInfo("de-DE")));
    }

    [Fact]
    public void FormatTotal_UsesBase1024AndTb()
    {
        var inv = CultureInfo.InvariantCulture;
        Assert.Equal("1.24 GB", SpeedFormatter.FormatTotal(1_331_439_861, inv));
        Assert.Equal("0 B", SpeedFormatter.FormatTotal(0, inv));
        Assert.Equal("512 B", SpeedFormatter.FormatTotal(512, inv));
        Assert.Equal("0.98 KB", SpeedFormatter.FormatTotal(1000, inv)); // 1000 B would display as 1000: rolls up
        Assert.Equal("1.00 KB", SpeedFormatter.FormatTotal(1023, inv));
        Assert.Equal("1.50 TB", SpeedFormatter.FormatTotal(1_649_267_441_664, inv));
    }

    [Fact]
    public void AccessibleSpeed_SpellsOutUnits()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("1.20 megabytes per second", SpeedFormatter.AccessibleSpeed(1_258_291, SpeedUnits.Bytes));
            Assert.Equal("10.0 megabits per second", SpeedFormatter.AccessibleSpeed(1_250_000, SpeedUnits.Bits));
            Assert.Equal("0 kilobytes per second", SpeedFormatter.AccessibleSpeed(0, SpeedUnits.Bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
