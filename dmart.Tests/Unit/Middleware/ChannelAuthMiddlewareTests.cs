using System.Text;
using Dmart.Config;
using Dmart.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Middleware;

// Unit tests for ChannelAuthMiddleware. Builds a minimal ASP.NET pipeline in
// memory (no TestServer / WebApplicationFactory) so each branch of the gate
// can be exercised against a synthetic HttpContext. Mirrors Python parity
// tests for utils/middleware.py::ChannelMiddleware.
public class ChannelAuthMiddlewareTests : IDisposable
{
    private readonly string _channelsJson = Path.Combine(
        Path.GetTempPath(),
        $"dmart-channels-mw-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_channelsJson)) File.Delete(_channelsJson);
        GC.SuppressFinalize(this);
    }

    private async Task<HttpContext> Run(
        bool enableChannelAuth,
        string channelsJsonContent,
        string path,
        string? channelKey = null,
        string[]? channelKeyValues = null)
    {
        File.WriteAllText(_channelsJson, channelsJsonContent);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddOptions<DmartSettings>().Configure(s =>
        {
            s.EnableChannelAuth = enableChannelAuth;
            s.ChannelsConfigPath = _channelsJson;
        });
        services.AddSingleton<ChannelsRegistry>();

        var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);
        app.UseChannelAuth();
        // Terminal handler — only reached when the gate passes; mark with 200
        // so the test can distinguish "passed through" from "rejected by gate".
        app.Run(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Request.Path = path;
        ctx.Request.Method = "GET";
        if (channelKeyValues is not null)
            ctx.Request.Headers["x-channel-key"] = channelKeyValues;
        else if (channelKey is not null)
            ctx.Request.Headers["x-channel-key"] = channelKey;
        ctx.Response.Body = new MemoryStream();

        await pipeline(ctx);
        ctx.Response.Body.Position = 0;
        return ctx;
    }

    private const string SingleChannelJson = """
        [
          {
            "name": "mobileapp",
            "keys": ["valid-key-1"],
            "allowed_api_patterns": ["/public/.*"]
          }
        ]
        """;

    [Fact]
    public async Task Disabled_Always_Passes()
    {
        var ctx = await Run(enableChannelAuth: false,
            channelsJsonContent: SingleChannelJson,
            path: "/public/anything",
            channelKey: null);
        ctx.Response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task NoHeader_PathMatches_Returns403()
    {
        var ctx = await Run(enableChannelAuth: true,
            channelsJsonContent: SingleChannelJson,
            path: "/public/foo",
            channelKey: null);
        ctx.Response.StatusCode.ShouldBe(403);
        var body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        body.ShouldContain("\"type\":\"channel_auth\"");
    }

    [Fact]
    public async Task NoHeader_PathOutsideAnyPattern_PassesThrough()
    {
        // No channel pattern protects /managed/*, so unauthenticated requests
        // to it must reach the handler — same as Python ChannelMiddleware.
        var ctx = await Run(enableChannelAuth: true,
            channelsJsonContent: SingleChannelJson,
            path: "/managed/entry",
            channelKey: null);
        ctx.Response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task ValidKey_PathMatches_PassesThrough()
    {
        var ctx = await Run(enableChannelAuth: true,
            channelsJsonContent: SingleChannelJson,
            path: "/public/foo",
            channelKey: "valid-key-1");
        ctx.Response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task ValidKey_PathOutsideOwnPatterns_Returns403()
    {
        // Key is recognized but the channel does not grant access to /managed/*.
        var ctx = await Run(enableChannelAuth: true,
            channelsJsonContent: SingleChannelJson,
            path: "/managed/entry",
            channelKey: "valid-key-1");
        ctx.Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task UnknownKey_Returns403()
    {
        var ctx = await Run(enableChannelAuth: true,
            channelsJsonContent: SingleChannelJson,
            path: "/public/foo",
            channelKey: "not-a-real-key");
        ctx.Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task MultipleChannels_KeyMatchesSecond_PathMatchesItsPattern()
    {
        const string twoChannels = """
            [
              { "name":"a", "keys":["k1"], "allowed_api_patterns":["/public/.*"] },
              { "name":"b", "keys":["k2"], "allowed_api_patterns":["/info/.*"] }
            ]
            """;
        var ctx = await Run(enableChannelAuth: true,
            channelsJsonContent: twoChannels,
            path: "/info/health",
            channelKey: "k2");
        ctx.Response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task MultiValue_Header_Uses_First_Value_Like_Python()
    {
        // A client that (intentionally or by proxy quirk) sends two
        // x-channel-key values must be matched on the FIRST value, like
        // Python's headers.get('x-channel-key'). The pre-fix code used
        // StringValues.ToString() which joined with commas — making
        // "valid-key-1, junk" the single key string and missing the
        // configured "valid-key-1" entry entirely.
        var ctx = await Run(enableChannelAuth: true,
            channelsJsonContent: SingleChannelJson,
            path: "/public/foo",
            channelKeyValues: new[] { "valid-key-1", "junk" });
        ctx.Response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Pathological_Pattern_Times_Out_And_Treated_As_NoMatch()
    {
        // ChannelsRegistry compiles patterns with a 100ms MatchTimeout.
        // SafeIsMatch in the middleware must catch RegexMatchTimeoutException
        // and treat the timeout as a non-match — translating to "path is not
        // restricted by this channel" for the no-header branch (pass through)
        // or "path not authorized for this channel" for the with-key branch
        // (403). Here we use the no-header branch: a path that triggers ReDoS
        // must not 500 the request — it should pass through to the handler.
        const string evilJson = """
            [
              {
                "name": "evil",
                "keys": ["k"],
                "allowed_api_patterns": ["^(a+)+$"]
              }
            ]
            """;
        var redosPath = "/" + new string('a', 30) + "b";
        var ctx = await Run(enableChannelAuth: true,
            channelsJsonContent: evilJson,
            path: redosPath,
            channelKey: null);
        // Pattern timeout → SafeIsMatch returns false → no channel claims
        // this path → unauthenticated request passes through.
        ctx.Response.StatusCode.ShouldBe(200);
    }
}
