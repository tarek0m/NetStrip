using System.Net;
using System.Text;
using NetStrip.Core;
using NetStrip.Core.Tests.Fakes;
using NetStrip.Core.Updates;

namespace NetStrip.Core.Tests.Updates;

public class UpdateCheckerTests
{
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLog _log = new();
    private readonly StubHandler _handler = new();

    private UpdateChecker Create(string slug = "owner/netstrip", Version? current = null) =>
        new(new HttpClient(_handler), slug, current ?? new Version(0, 1, 0), _clock, _log);

    private static string Release(string tag, bool prerelease = false, string? url = "https://github.com/owner/netstrip/releases/tag/TAG") =>
        $$"""{ "tag_name": "{{tag}}", "prerelease": {{(prerelease ? "true" : "false")}}, "draft": false{{(url is null ? "" : $$""", "html_url": "{{url.Replace("TAG", tag)}}" """)}} }""";

    [Fact]
    public async Task NewerTag_ReturnsInfo()
    {
        _handler.Json(Release("v0.2.0"));

        var info = await Create().CheckAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(new Version(0, 2, 0), info.Version);
        Assert.Equal(new Uri("https://github.com/owner/netstrip/releases/tag/v0.2.0"), info.ReleaseUrl);
    }

    [Fact]
    public async Task NewerTag_SendsExpectedRequest()
    {
        _handler.Json(Release("v0.2.0"));

        await Create().CheckAsync(CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/owner/netstrip/releases/latest", request.RequestUri!.ToString());
        Assert.Equal("NetStrip/0.1.0", request.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData("v0.10.0", true)]     // numeric, not lexical, comparison
    [InlineData("0.1.1", true)]       // no leading v
    [InlineData("V1.0", true)]        // two components
    [InlineData("v0.1.0", false)]
    [InlineData("v0.0.9", false)]
    public async Task TagComparison(string tag, bool newer)
    {
        _handler.Json(Release(tag));

        var info = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(newer, info is not null);
    }

    [Fact]
    public async Task Prerelease_Ignored()
    {
        _handler.Json(Release("v0.3.0", prerelease: true));

        Assert.Null(await Create().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PrereleaseSuffixInTag_Ignored()
    {
        _handler.Json(Release("v0.3.0-beta.1"));

        Assert.Null(await Create().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SameVersion_Null()
    {
        _handler.Json(Release("v0.1.0"));

        Assert.Null(await Create().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SameVersion_AssemblyVersionWithRevision_Null()
    {
        _handler.Json(Release("v0.1.0"));

        Assert.Null(await Create(current: new Version(0, 1, 0, 0)).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingHtmlUrl_FallsBackToTagPage()
    {
        _handler.Json(Release("v0.2.0", url: null));

        var info = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(new Uri("https://github.com/owner/netstrip/releases/tag/v0.2.0"), info!.ReleaseUrl);
    }

    [Fact]
    public async Task NonHttpsHtmlUrl_FallsBackToTagPage()
    {
        _handler.Json(Release("v0.2.0", url: "file:///c:/evil/TAG"));

        var info = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(new Uri("https://github.com/owner/netstrip/releases/tag/v0.2.0"), info!.ReleaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptySlug_NoRequest(string slug)
    {
        Assert.Null(await Create(slug).CheckAsync(CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task HttpFailure_NullAndLogged()
    {
        _handler.Fail(new HttpRequestException("no network"));

        Assert.Null(await Create().CheckAsync(CancellationToken.None));
        Assert.NotEmpty(_log.Warnings);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ErrorStatus_NullAndLogged(HttpStatusCode status)
    {
        _handler.Respond(status, "{}");

        Assert.Null(await Create().CheckAsync(CancellationToken.None));
        Assert.NotEmpty(_log.Warnings);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": "banana" }""")]
    [InlineData("""{ "tag_name": 5 }""")]
    public async Task MalformedBody_NullAndLogged(string body)
    {
        _handler.Json(body);

        Assert.Null(await Create().CheckAsync(CancellationToken.None));
        Assert.NotEmpty(_log.Warnings);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _handler.Json(Release("v0.2.0"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().CheckAsync(cts.Token));
    }

    [Fact]
    public async Task SecondCallWithin24h_NoRequest()
    {
        _handler.Json(Release("v0.2.0"));
        var checker = Create();

        await checker.CheckAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(23));
        var second = await checker.CheckAsync(CancellationToken.None);

        Assert.Null(second);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task After24h_ChecksAgain()
    {
        _handler.Json(Release("v0.2.0"));
        var checker = Create();

        await checker.CheckAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(24));
        var second = await checker.CheckAsync(CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task FailedCheck_AlsoCountsTowardsTheDailyLimit()
    {
        _handler.Fail(new HttpRequestException("no network"));
        var checker = Create();

        await checker.CheckAsync(CancellationToken.None);
        await checker.CheckAsync(CancellationToken.None);

        Assert.Single(_handler.Requests);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _respond = () => new HttpResponseMessage(HttpStatusCode.OK);

        public List<HttpRequestMessage> Requests { get; } = new();

        public void Json(string body) => Respond(HttpStatusCode.OK, body);

        public void Respond(HttpStatusCode status, string body) =>
            _respond = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public void Fail(Exception ex) => _respond = () => throw ex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_respond());
        }
    }
}
