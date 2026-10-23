using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetStrip.Core;

public enum AdapterKind { Ethernet, WiFi, Cellular, Other }
public sealed record AdapterInfo(string Id, string Name, AdapterKind Kind, string? IPv4);
public sealed record Sample(DateTimeOffset Time, string AdapterId, long UpBytesPerSec, long DownBytesPerSec);
public static class AdapterIds { public const string All = "all"; public const string Auto = "auto"; }

public enum SpeedUnits { Bytes, Bits }
public enum MonitorScope { Primary, All }
public enum ThemeSetting { System, Light, Dark }
public enum TaskbarEdge { Bottom, Top, Left, Right }
public enum CapLevel { None, Warning80, Reached100 }
public readonly record struct PixelPoint(int X, int Y);
public readonly record struct Totals(long UpBytes, long DownBytes);
public sealed record UpdateInfo(Version Version, Uri ReleaseUrl);

// §10 settings model: exact keys/defaults; JSON camelCase; unknown fields kept in Extra
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public SpeedUnits Units { get; set; } = SpeedUnits.Bytes;
    public string Adapter { get; set; } = AdapterIds.Auto;
    public MonitorScope Monitors { get; set; } = MonitorScope.Primary;
    public bool StartWithWindows { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool IdleFade { get; set; } = true;
    public ThemeSetting Theme { get; set; } = ThemeSetting.System;
    public double? CapGb { get; set; }
    public int CapResetDay { get; set; } = 1;
    public string CapAdapter { get; set; } = AdapterIds.All;
    public bool CheckForUpdates { get; set; } = true;
    public PixelPoint? FloatingPosition { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    void Update(Action<AppSettings> change);
    event Action<AppSettings>? Changed;
}

public interface IUsageQuery
{
    Totals Today(string scope);
    Totals ThisPeriod(string scope);
    (long Used, long Cap)? CapProgress();
    string? LoadNotice { get; }
}

// Raw counters for one adapter at one instant (Shell implements, Core consumes)
public sealed record InterfaceCounters(string Id, string Name, AdapterKind Kind, string? IPv4,
    ulong InOctets, ulong OutOctets, bool Included);

public interface IInterfaceTable
{
    IReadOnlyList<InterfaceCounters> Read();
    string? BestInterfaceId();                 // GetBestInterfaceEx toward 1.1.1.1; null if none
}

public interface IClock
{
    DateTimeOffset Now { get; }
    long TimestampTicks { get; }
    double TicksPerSecond { get; }
}

public interface IFileSystem
{
    string? ReadAllText(string path);          // null if missing
    void WriteAllTextAtomic(string path, string contents); // temp + File.Replace / File.Move
    void Move(string from, string to);
}

public interface ILog
{
    void Info(string msg);
    void Warn(string msg, Exception? ex = null);
    void Error(string msg, Exception? ex = null);
}

public interface ISampleSource
{
    event Action<Sample>? SampleProduced;
    AdapterInfo? CurrentAdapter { get; }
    IReadOnlyList<AdapterInfo> Adapters { get; }
    bool Paused { get; set; }
}

public enum StripMode { Full, Compact, Floating }
public readonly record struct PixelRect(int X, int Y, int Width, int Height);
public sealed record StripPlacement(nint ParentHwnd, PixelRect Bounds, StripMode Mode, double DpiScale);

// Bounds are relative to ParentHwnd's client area for Full/Compact, screen coords for Floating (ParentHwnd = 0)
public interface IStripSurface
{
    nint Handle { get; }
    (int Width, int Height) Measure(StripMode mode, double dpiScale, int taskbarThickness);
    void Apply(StripPlacement placement);
    void SetHidden(bool hidden);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public long TimestampTicks => Stopwatch.GetTimestamp();
    public double TicksPerSecond => Stopwatch.Frequency;
}

public sealed class RealFileSystem : IFileSystem
{
    public string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void WriteAllTextAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public void Move(string from, string to) => File.Move(from, to, overwrite: true);
}
