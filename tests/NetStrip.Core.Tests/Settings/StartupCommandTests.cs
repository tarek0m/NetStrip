using NetStrip.Core.Settings;

namespace NetStrip.Core.Tests.Settings;

public class StartupCommandTests
{
    private const string Exe = @"C:\Users\tarek\AppData\Local\Programs\NetStrip\NetStrip.exe";

    [Fact]
    public void For_QuotesThePath()
    {
        Assert.Equal("\"" + Exe + "\"", StartupCommand.For(Exe));
    }

    [Theory]
    [InlineData("\"C:\\Users\\tarek\\AppData\\Local\\Programs\\NetStrip\\NetStrip.exe\"")]   // app and installer format
    [InlineData("C:\\Users\\tarek\\AppData\\Local\\Programs\\NetStrip\\NetStrip.exe")]       // unquoted
    [InlineData("\"c:\\users\\TAREK\\appdata\\local\\programs\\netstrip\\NETSTRIP.EXE\"")]   // case-insensitive
    [InlineData("  \"C:\\Users\\tarek\\AppData\\Local\\Programs\\NetStrip\\NetStrip.exe\" --minimized ")]   // arguments
    public void Starts_ThisExe(string runValue)
    {
        Assert.True(StartupCommand.Starts(runValue, Exe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\Users\\tarek\\source\\NetStrip\\src\\NetStrip\\bin\\Release\\net10.0-windows10.0.19041.0\\NetStrip.exe\"")]   // an old dev build
    [InlineData("\"C:\\Program Files\\Other\\Other.exe\"")]
    public void DoesNotStart_ThisExe(string? runValue)
    {
        Assert.False(StartupCommand.Starts(runValue, Exe));
    }

    [Fact]
    public void For_RoundTripsThroughStarts()
    {
        const string withSpaces = @"C:\Program Files (x86)\Net Strip\NetStrip.exe";
        Assert.True(StartupCommand.Starts(StartupCommand.For(withSpaces), withSpaces));
    }
}
