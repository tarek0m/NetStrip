using System.Net.Http.Headers;
using System.Text.Json;

namespace NetStrip.Core.Updates;

/// <summary>
/// Asks GitHub for the latest release (§11). At most one request per 24 h (failed attempts count too); never downloads or installs.
/// Failures are logged and reported as "no update".
/// </summary>
public sealed class UpdateChecker(HttpClient http, string repoSlug, Version current, IClock clock, ILog log)
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private DateTimeOffset? _lastCheck;

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoSlug)) return null;

        lock (_gate)
        {
            var now = clock.Now;
            if (_lastCheck is { } last && now - last < MinInterval) return null;
            _lastCheck = now;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repoSlug.Trim()}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NetStrip", Normalize(current).ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return Evaluate(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException)
        {
            log.Warn("Update check failed", ex);
            return null;
        }
    }

    private UpdateInfo? Evaluate(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) throw new InvalidDataException("release payload is not an object");
        if (!release.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("release has no tag_name");

        var tag = tagElement.GetString()!;
        if (IsTrue(release, "prerelease") || IsTrue(release, "draft")) return null;

        var latest = ParseTag(tag) ?? throw new InvalidDataException($"unrecognised release tag '{tag}'");
        if (latest.IsPrerelease) return null;
        if (Normalize(latest.Version) <= Normalize(current)) return null;

        return new UpdateInfo(latest.Version, ReleaseUrl(release, tag));
    }

    private Uri ReleaseUrl(JsonElement release, string tag)
    {
        if (release.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return uri;
        return new Uri($"https://github.com/{repoSlug.Trim()}/releases/tag/{Uri.EscapeDataString(tag)}");
    }

    private static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // "v1.2.3", "1.2", "v1.2.3+build" parse; "v1.2.3-beta.1" parses as a prerelease.
    private static (Version Version, bool IsPrerelease)? ParseTag(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        var dash = text.IndexOf('-');
        var prerelease = dash >= 0;
        if (prerelease) text = text[..dash];

        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var version) ? (version, prerelease) : null;
    }

    // Version treats an absent component (-1) as older than 0, so 0.1.0 < 0.1.0.0 unless both are padded.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
