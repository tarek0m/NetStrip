using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetStrip.Core.Settings;

/// <summary>
/// Reads and writes <c>settings.json</c> (§10). <see cref="Current"/> is an immutable-by-convention snapshot:
/// <see cref="Update"/> applies the change to a copy and swaps it in, so earlier snapshots never change underneath a reader.
/// </summary>
public sealed class SettingsStore(string path, IFileSystem fs, ILog log, IClock? clock = null) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly object _gate = new();
    private readonly IClock _clock = clock ?? new SystemClock();
    private volatile AppSettings _current = new();
    private bool _unreadFile;   // settings.json exists but could not be read: saving would overwrite the user's settings

    public AppSettings Current => _current;

    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        lock (_gate)
        {
            AppSettings loaded;
            string? text = null;
            _unreadFile = false;
            try
            {
                text = FileReads.ReadWithRetry(fs, path);
            }
            catch (Exception ex)
            {
                // Locked or inaccessible, not proven invalid: use defaults for this session and leave the file alone.
                _unreadFile = true;
                log.Error($"Could not read settings file {path}; using defaults for this session without saving over it", ex);
            }

            try
            {
                loaded = text is null
                    ? new AppSettings()
                    : JsonSerializer.Deserialize<AppSettings>(text, JsonOptions) ?? throw new InvalidDataException("settings file is empty");
            }
            catch (Exception ex)
            {
                BackUp(ex);
                loaded = new AppSettings();
            }

            Normalize(loaded);
            _current = loaded;
            return loaded;
        }
    }

    public void Save()
    {
        lock (_gate) Write(_current);
    }

    public void Update(Action<AppSettings> change)
    {
        AppSettings next;
        lock (_gate)
        {
            next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_current, JsonOptions), JsonOptions)!;
            change(next);
            Normalize(next);
            _current = next;
            Write(next);
        }
        Changed?.Invoke(next);
    }

    private void Write(AppSettings settings)
    {
        if (_unreadFile)
        {
            log.Warn($"Not saving settings: {path} could not be read at startup, and saving would replace it");
            return;
        }
        try
        {
            fs.WriteAllTextAtomic(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            log.Error($"Could not save settings to {path}", ex);
        }
    }

    private void BackUp(Exception cause)
    {
        var bad = path + ".bad-" + _clock.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        log.Error($"Settings file {path} is invalid; using defaults and keeping a copy as {bad}", cause);
        try
        {
            fs.Move(path, bad);
        }
        catch (Exception moveEx)
        {
            log.Error($"Could not back up the invalid settings file to {bad}", moveEx);
        }
    }

    private static void Normalize(AppSettings s)
    {
        s.CapResetDay = Math.Clamp(s.CapResetDay, 1, 28);
        if (string.IsNullOrEmpty(s.Adapter)) s.Adapter = AdapterIds.Auto;
        if (string.IsNullOrEmpty(s.CapAdapter)) s.CapAdapter = AdapterIds.All;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
