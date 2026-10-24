namespace NetStrip.Core.Tests;

public class ExitAfterOptionTests
{
    [Fact]
    public void Absent_IsNullAndNotIgnored()
    {
        Assert.Null(ExitAfterOption.Parse(["--fake"], out var ignored));
        Assert.False(ignored);
        Assert.Null(ExitAfterOption.Parse([], out ignored));
        Assert.False(ignored);
    }

    [Theory]
    [InlineData(20.0, "--exit-after", "20")]
    [InlineData(20.0, "--fake", "--exit-after", "20")]
    [InlineData(20.0, "--exit-after", "20", "--fake")]
    [InlineData(5.0, "--EXIT-AFTER", "5")]
    [InlineData(0.5, "--exit-after", "0.5")]
    [InlineData(86400.0, "--exit-after", "86400")]
    [InlineData(3.0, "--exit-after", "abc", "--exit-after", "3")]   // last one decides
    public void Valid_ReturnsTheDelay(double seconds, params string[] args)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ExitAfterOption.Parse(args, out var ignored));
        Assert.False(ignored);
    }

    [Theory]
    [InlineData("--exit-after")]                              // value missing
    [InlineData("--exit-after", "--fake")]                    // next option is not a value
    [InlineData("--exit-after", "")]
    [InlineData("--exit-after", "twenty")]
    [InlineData("--exit-after", "0")]
    [InlineData("--exit-after", "-5")]
    [InlineData("--exit-after", "86401")]                     // over a day
    [InlineData("--exit-after", "NaN")]
    [InlineData("--exit-after", "Infinity")]
    [InlineData("--exit-after", "1e400")]                     // overflows to infinity
    [InlineData("--exit-after", "2,5")]                       // invariant culture only
    [InlineData("--exit-after", "3", "--exit-after", "x")]    // last one decides
    public void Invalid_IsNullAndIgnored(params string[] args)
    {
        Assert.Null(ExitAfterOption.Parse(args, out var ignored));
        Assert.True(ignored);
    }
}
