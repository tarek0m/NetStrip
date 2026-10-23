using System.Globalization;
using System.Text.Json;

namespace NetStrip.Core.Usage;

/// <summary>
/// Daily per-adapter byte totals keyed by local date, persisted to usage.json (§5).
/// All members are thread-safe; <see cref="CapThresholdCrossed"/> is raised on the thread that called <see cref="Add"/>.
/// </summary>
public sealed class UsageStore(string path, IClock clock, IFileSystem fs, ILog log) : IUsageQuery
{
    private const int RetentionMonths = 13;
    // A date is pruned only if it is also older than the 397th most recent date with data (≈ the most days 13 months
    // hold). A clock that jumps forward adds one new date, so it can never wipe the history (spec §5 intent).
    private const int MinDatesKept = 397;
    private const string DateFormat = "yyyy-MM-dd";
    private const double BytesPerGb = 1024.0 * 1024 * 1024;   // same 1024 base as the totals display

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<string, SortedDictionary<DateOnly, long[]>> _adapters = new(StringComparer.Ordinal);
    private readonly Dictionary<DateOnly, List<CapLevel>> _capFired = new();   // keyed by period start

    private int _capResetDay = 1;
    private string _capScope = AdapterIds.All;
    private double? _capGb;
    private bool _dirty;
    private DateOnly _lastPruned;
    private bool _unreadFile;      // usage.json exists but could not be read: never overwrite it until it has been
    private bool _unreadLogged;

    public string? LoadNotice { get; private set; }

    public int CapResetDay
    {
        get { lock (_gate) return _capResetDay; }
        set { lock (_gate) _capResetDay = Math.Clamp(value, 1, 28); }
    }

    public double? CapGb
    {
        get { lock (_gate) return _capGb; }
        set { lock (_gate) _capGb = value; }
    }

    public string CapScope
    {
        get { lock (_gate) return _capScope; }
        set { lock (_gate) _capScope = string.IsNullOrEmpty(value) ? AdapterIds.All : value; }
    }

    /// <summary>Raised once per level per period, after the sample that crossed it was added.</summary>
    public event Action<CapLevel>? CapThresholdCrossed;

    public void Load()
    {
        lock (_gate)
        {
            _adapters.Clear();
            _capFired.Clear();
            _dirty = false;
            _unreadFile = false;
            LoadNotice = null;

            string? text;
            try
            {
                text = FileReads.ReadWithRetry(fs, path);
            }
            catch (Exception ex)
            {
                // Locked or inaccessible, not proven corrupt: keep the file and merge it in on a later save.
                _unreadFile = true;
                _unreadLogged = true;
                log.Error($"Could not read usage file {path}; leaving it untouched and retrying before the next save", ex);
                return;
            }
            if (text is null) return;

            try
            {
                Parse(text, _adapters, _capFired);
            }
            catch (Exception ex)
            {
                _adapters.Clear();
                _capFired.Clear();
                Quarantine(ex);
                return;
            }

            Prune(LocalToday());
        }
    }

    public void Save()
    {
        lock (_saveGate)
        {
            string json;
            lock (_gate)
            {
                if (!_dirty) return;
                if (_unreadFile && !TryMergeUnreadFile()) return;   // keep the data in memory; never clobber the file
                json = Serialize();
                _dirty = false;
            }

            try
            {
                fs.WriteAllTextAtomic(path, json);
            }
            catch (Exception ex)
            {
                lock (_gate) _dirty = true;
                log.Error($"Could not save usage data to {path}", ex);
            }
        }
    }

    /// <summary>Adds one second's worth of traffic to the current local day.</summary>
    public void Add(Sample s)
    {
        var up = Math.Max(0, s.UpBytesPerSec);
        var down = Math.Max(0, s.DownBytesPerSec);
        if (up == 0 && down == 0) return;

        CapLevel? crossed;
        lock (_gate)
        {
            var today = LocalToday();
            if (today != _lastPruned) Prune(today);

            if (!_adapters.TryGetValue(s.AdapterId, out var days))
                _adapters[s.AdapterId] = days = new SortedDictionary<DateOnly, long[]>();
            if (!days.TryGetValue(today, out var bucket))
                days[today] = bucket = new long[2];
            bucket[0] += up;
            bucket[1] += down;
            _dirty = true;

            crossed = EvaluateCap(today);
        }

        if (crossed is not { } level) return;
        try
        {
            CapThresholdCrossed?.Invoke(level);
        }
        catch (Exception ex)
        {
            // Add runs inside the sampler chain; a failing subscriber must not break counting.
            log.Error("A data cap warning handler failed", ex);
        }
    }

    public Totals Today(string scope)
    {
        lock (_gate)
        {
            var today = LocalToday();
            return Sum(scope, today, today);
        }
    }

    public Totals ThisPeriod(string scope)
    {
        lock (_gate)
        {
            var (start, end) = PeriodFor(LocalToday(), _capResetDay);
            return Sum(scope, start, end);
        }
    }

    public (long Used, long Cap)? CapProgress()
    {
        lock (_gate)
        {
            if (_capGb is not > 0) return null;
            var (start, end) = PeriodFor(LocalToday(), _capResetDay);
            var used = Sum(_capScope, start, end);
            return (used.UpBytes + used.DownBytes, CapBytes(_capGb.Value));
        }
    }

    private DateOnly LocalToday() => DateOnly.FromDateTime(clock.Now.DateTime);

    private static long CapBytes(double capGb) => (long)Math.Min(capGb * BytesPerGb, long.MaxValue);

    // Period runs from resetDay of this month (or last month, if resetDay is still ahead) to the day before the next reset.
    private static (DateOnly Start, DateOnly End) PeriodFor(DateOnly date, int resetDay)
    {
        var start = new DateOnly(date.Year, date.Month, resetDay);
        if (date.Day < resetDay) start = start.AddMonths(-1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    private Totals Sum(string scope, DateOnly from, DateOnly to)
    {
        long up = 0, down = 0;
        if (scope == AdapterIds.All)
        {
            // Every adapter key plus the "all" key (samples taken while the sampler was in all-adapters mode).
            foreach (var days in _adapters.Values) SumDays(days, from, to, ref up, ref down);
        }
        else if (_adapters.TryGetValue(scope, out var days))
        {
            SumDays(days, from, to, ref up, ref down);
        }
        return new Totals(up, down);
    }

    private static void SumDays(SortedDictionary<DateOnly, long[]> days, DateOnly from, DateOnly to, ref long up, ref long down)
    {
        if (from == to)
        {
            if (days.TryGetValue(from, out var bucket)) { up += bucket[0]; down += bucket[1]; }
            return;
        }
        foreach (var (date, bucket) in days)
        {
            if (date < from) continue;
            if (date > to) break;
            up += bucket[0];
            down += bucket[1];
        }
    }

    // Each level fires once per period. Jumping straight past 100 % reports only Reached100.
    private CapLevel? EvaluateCap(DateOnly today)
    {
        if (_capGb is not > 0) return null;

        var (start, end) = PeriodFor(today, _capResetDay);
        var used = Sum(_capScope, start, end);
        var ratio = (double)(used.UpBytes + used.DownBytes) / CapBytes(_capGb.Value);

        CapLevel level = ratio >= 1.0 ? CapLevel.Reached100 : ratio >= 0.8 ? CapLevel.Warning80 : CapLevel.None;
        if (level == CapLevel.None) return null;

        if (!_capFired.TryGetValue(start, out var fired))
            _capFired[start] = fired = new List<CapLevel>();
        if (fired.Contains(level)) return null;

        if (level == CapLevel.Reached100 && !fired.Contains(CapLevel.Warning80)) fired.Add(CapLevel.Warning80);
        fired.Add(level);
        _dirty = true;
        return level;
    }

    private void Prune(DateOnly today)
    {
        _lastPruned = today;
        var calendarCutoff = today.AddMonths(-RetentionMonths);
        var keptByCount = _adapters.Values.SelectMany(days => days.Keys).Distinct()
            .OrderDescending().Skip(MinDatesKept - 1).FirstOrDefault();   // MinValue while fewer dates exist
        var cutoff = keptByCount < calendarCutoff ? keptByCount : calendarCutoff;

        foreach (var (id, days) in _adapters)
        {
            while (days.Count > 0 && days.Keys.First() < cutoff)
            {
                days.Remove(days.Keys.First());
                _dirty = true;
            }
            if (days.Count == 0) _adapters.Remove(id);
        }

        foreach (var periodStart in _capFired.Keys.Where(k => k < cutoff).ToList())
        {
            _capFired.Remove(periodStart);
            _dirty = true;
        }
    }

    /// <summary>
    /// Called under <c>_gate</c> before saving when Load could not read the file. Merges what the file holds into
    /// memory (or quarantines it if it turns out to be corrupt). False while it is still unreadable.
    /// </summary>
    private bool TryMergeUnreadFile()
    {
        string? text;
        try
        {
            text = FileReads.ReadWithRetry(fs, path);
        }
        catch (Exception ex)
        {
            if (!_unreadLogged) log.Warn($"Usage file {path} is still unreadable; not saving over it", ex);
            _unreadLogged = true;
            return false;
        }

        _unreadFile = false;
        if (text is null) return true;

        var adapters = new Dictionary<string, SortedDictionary<DateOnly, long[]>>(StringComparer.Ordinal);
        var capFired = new Dictionary<DateOnly, List<CapLevel>>();
        try
        {
            Parse(text, adapters, capFired);
        }
        catch (Exception ex)
        {
            Quarantine(ex);
            return true;
        }

        foreach (var (id, fileDays) in adapters)
        {
            if (!_adapters.TryGetValue(id, out var days)) _adapters[id] = days = new SortedDictionary<DateOnly, long[]>();
            foreach (var (date, pair) in fileDays)
            {
                if (days.TryGetValue(date, out var bucket))
                {
                    bucket[0] += pair[0];
                    bucket[1] += pair[1];
                }
                else
                {
                    days[date] = pair;
                }
            }
        }
        foreach (var (periodStart, levels) in capFired)
        {
            if (!_capFired.TryGetValue(periodStart, out var fired)) _capFired[periodStart] = fired = [];
            foreach (var level in levels.Where(l => !fired.Contains(l))) fired.Add(level);
        }
        log.Info($"Usage file {path} is readable again; merged it with this session's usage");
        Prune(LocalToday());
        return true;
    }

    private static void Parse(string text, Dictionary<string, SortedDictionary<DateOnly, long[]>> adaptersOut,
        Dictionary<DateOnly, List<CapLevel>> capFiredOut)
    {
        var file = JsonSerializer.Deserialize<UsageFile>(text, JsonOptions)
                   ?? throw new InvalidDataException("usage file is empty");

        if (file.Adapters is not null)
        {
            foreach (var (id, days) in file.Adapters)
            {
                var parsed = new SortedDictionary<DateOnly, long[]>();
                foreach (var (dateText, pair) in days)
                {
                    if (pair is not { Length: 2 } || pair[0] < 0 || pair[1] < 0)
                        throw new InvalidDataException($"bad bucket for {id} {dateText}");
                    parsed[ParseDate(dateText)] = [pair[0], pair[1]];
                }
                adaptersOut[id] = parsed;
            }
        }

        if (file.CapFired is not null)
        {
            foreach (var (dateText, names) in file.CapFired)
            {
                var levels = new List<CapLevel>();
                foreach (var name in names)
                {
                    // Unknown names are skipped so a newer file does not read as corrupt.
                    if (Enum.TryParse<CapLevel>(name, out var level) && level != CapLevel.None && !levels.Contains(level))
                        levels.Add(level);
                }
                capFiredOut[ParseDate(dateText)] = levels;
            }
        }
    }

    private static DateOnly ParseDate(string text) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new InvalidDataException($"bad date '{text}'");

    private string Serialize()
    {
        var file = new UsageFile
        {
            Adapters = _adapters
                .Where(a => a.Value.Count > 0)
                .ToDictionary(
                    a => a.Key,
                    a => a.Value.ToDictionary(d => d.Key.ToString(DateFormat, CultureInfo.InvariantCulture), d => d.Value)),
            CapFired = _capFired.ToDictionary(
                f => f.Key.ToString(DateFormat, CultureInfo.InvariantCulture),
                f => f.Value.Select(l => l.ToString()).ToList()),
        };
        return JsonSerializer.Serialize(file, JsonOptions);
    }

    private void Quarantine(Exception cause)
    {
        var bad = path + ".bad-" + clock.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        log.Error($"Usage file {path} is unreadable; starting empty", cause);
        try
        {
            fs.Move(path, bad);
            LoadNotice = $"Your usage history could not be read, so it was reset. The old file was kept as {Path.GetFileName(bad)}.";
        }
        catch (Exception moveEx)
        {
            log.Error($"Could not move the unreadable usage file to {bad}", moveEx);
            LoadNotice = "Your usage history could not be read, so it was reset.";
        }
    }
}
