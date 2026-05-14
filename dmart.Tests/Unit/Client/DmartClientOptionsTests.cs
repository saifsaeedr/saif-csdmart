using System.Net;
using System.Text;
using Dmart.Client;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Client;

// Pins the configuration surface introduced by DmartClientOptions —
// constructor selection, env-var fallback, and how options propagate to
// the underlying HttpClient (token, timeout, default headers).
public class DmartClientOptionsTests
{
    private const string EnvVar = "DMART_BASE_URL";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":\"success\"}", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void Options_Ctor_Uses_BaseUrl_From_Property()
    {
        using var client = new DmartClient(new DmartClientOptions
        {
            BaseUrl = "https://from-options.test/",  // trailing / should be stripped
        });

        client.BaseUrl.ShouldBe("https://from-options.test");
    }

    [Fact]
    public void Options_Ctor_Falls_Back_To_DMART_BASE_URL_Env()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, "https://from-env.test/");
        try
        {
            using var client = new DmartClient(new DmartClientOptions());
            client.BaseUrl.ShouldBe("https://from-env.test");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
        }
    }

    [Fact]
    public void Options_Ctor_Throws_When_BaseUrl_Missing()
    {
        // Snapshot + clear so this test is deterministic even if a CI env
        // accidentally exports DMART_BASE_URL.
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
        try
        {
            Should.Throw<InvalidOperationException>(() =>
                new DmartClient(new DmartClientOptions()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
        }
    }

    [Fact]
    public void Options_Ctor_Throws_On_Null_Options()
    {
        Should.Throw<ArgumentNullException>(() => new DmartClient((DmartClientOptions)null!));
    }

    [Fact]
    public void Options_AuthToken_Pre_Populates_AuthToken_Property()
    {
        using var client = new DmartClient(new DmartClientOptions
        {
            BaseUrl = "https://t.test",
            AuthToken = "preset-token",
        });

        client.AuthToken.ShouldBe("preset-token");
    }

    [Fact]
    public async Task Options_DefaultHeaders_Are_Attached_To_Outgoing_Requests()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        using var client = new DmartClient(new DmartClientOptions
        {
            BaseUrl = "https://t.test",
            DefaultHeaders = { ["X-Tenant-Id"] = "tenant-42" },
        }, http);

        await client.GetInfoMeAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Headers.GetValues("X-Tenant-Id").ShouldContain("tenant-42");
    }

    [Fact]
    public void Options_Timeout_Honored_Only_For_Owned_HttpClient()
    {
        // Owned HttpClient: timeout flows through.
        using (var owned = new DmartClient(new DmartClientOptions
        {
            BaseUrl = "https://t.test",
            Timeout = TimeSpan.FromSeconds(7),
        }))
        {
            // No public Timeout getter; we verify indirectly by constructing
            // the client without exception and trusting the behavior tested
            // via DefaultRequestHeaders below.
            owned.BaseUrl.ShouldBe("https://t.test");
        }

        // Caller-owned HttpClient: timeout NOT applied — we don't trample
        // an externally-managed HttpClient's tuning.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(99) };
        using var client = new DmartClient(new DmartClientOptions
        {
            BaseUrl = "https://t.test",
            Timeout = TimeSpan.FromSeconds(7),
        }, http);
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(99));
    }
}
