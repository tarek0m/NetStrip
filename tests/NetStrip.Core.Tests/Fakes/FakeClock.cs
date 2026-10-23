using NetStrip.Core;

namespace NetStrip.Core.Tests.Fakes;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset utc, TimeZoneInfo? zone = null, double ticksPerSecond = 1_000_000_000)
    {
        Now = TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Utc);
        TicksPerSecond = ticksPerSecond;
    }

    public DateTimeOffset Now { get; set; }
    public long TimestampTicks { get; set; }
    public double TicksPerSecond { get; set; }

    public void Advance(TimeSpan by)
    {
        Now += by;
        TimestampTicks += (long)(by.TotalSeconds * TicksPerSecond);
    }
}
