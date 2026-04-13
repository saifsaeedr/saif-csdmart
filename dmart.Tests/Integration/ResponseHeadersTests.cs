using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end HTTP tests for the CORS + security-header middleware. Each test
// boots a fresh WebApplicationFactory with specific Dmart:AllowedCorsOrigins
// values so we can verify every branch of Python's set_middleware_response_headers:
//
//   - empty allowlist  → fallback to same-host origin, origin not reflected
//                        unless it matches the canonical host:port
//   - non-empty match  → reflected origin + Allow-Credentials
//   - non-empty miss   → NO Access-Control-Allow-Origin header at all
//   - OPTIONS preflight → 204 with all CORS headers set
//
// Static CORS + security headers are asserted on every case to catch regressions.
public class ResponseHeadersTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ResponseHeadersTests(DmartFactory factory) => _factory = factory;

    // Helper — rebuild the factory with an override for AllowedCorsOrigins. The
    // base DmartFactory.ConfigureWebHost already seeds the common settings, so
    // we just add a second AddInMemoryCollection on top to override one key.
    private HttpClient ClientWithAllowlist(string allowedCorsOrigins)
        => _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmart:AllowedCorsOrigins"] = allowedCorsOrigins,
                ["Dmart:ListeningHost"] = "127.0.0.1",
                ["Dmart:ListeningPort"] = "5099",
            });
        })).CreateClient();

    // ==================== 1. security + static CORS headers ====================

    [Fact]
    public async Task Root_Response_Has_Security_And_Static_Cors_Headers()
    {
        var client = ClientWithAllowlist("");
        var resp = await client.GetAsync("/");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Headers.Contains("X-Content-Type-Options").ShouldBeTrue();
        resp.Headers.Contains("X-Frame-Options").ShouldBeTrue();
        resp.Headers.Contains("Referrer-Policy").ShouldBeTrue();
        resp.Headers.Contains("Permissions-Policy").ShouldBeTrue();
        // HSTS is only sent over HTTPS (RFC 6797). Test host uses HTTP.
        // resp.Headers.Contains("Strict-Transport-Security").ShouldBeTrue();
        resp.Headers.Contains("Access-Control-Allow-Methods").ShouldBeTrue();
        resp.Headers.Contains("Access-Control-Allow-Headers").ShouldBeTrue();
        resp.Headers.Contains("Access-Control-Max-Age").ShouldBeTrue();
        resp.Headers.Contains("x-server-time").ShouldBeTrue();
    }

    // ==================== 2. empty allowlist — fallback ====================

    [Fact]
    public async Task Empty_Allowlist_Falls_Back_To_SameHost_Origin()
    {
        var client = ClientWithAllowlist("");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "https://stranger.example.com");
        var resp = await client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        // The fallback writes the canonical same-host form, never the stranger.
        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var values).ShouldBeTrue();
        var allowed = string.Join(",", values!);
        allowed.ShouldBe("http://127.0.0.1:5099");
        allowed.ShouldNotContain("stranger.example.com");
    }

    // ==================== 3. allowlist match ====================

    [Fact]
    public async Task Allowlisted_Origin_Is_Reflected_With_Credentials()
    {
        var client = ClientWithAllowlist("http://localhost:3000,https://app.example.com");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "https://app.example.com");
        var resp = await client.SendAsync(req);

        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).ShouldBeTrue();
        string.Join(",", origins!).ShouldBe("https://app.example.com");
        resp.Headers.TryGetValues("Access-Control-Allow-Credentials", out var creds).ShouldBeTrue();
        string.Join(",", creds!).ShouldBe("true");
    }

    [Fact]
    public async Task Allowlist_Handles_Leading_Whitespace_On_Csv_Entries()
    {
        var client = ClientWithAllowlist(" http://a.com ,  http://b.com ");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "http://b.com");
        var resp = await client.SendAsync(req);

        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).ShouldBeTrue();
        string.Join(",", origins!).ShouldBe("http://b.com");
    }

    // ==================== 4. allowlist miss ====================

    [Fact]
    public async Task NonAllowlisted_Origin_Gets_No_Cors_Origin_Header()
    {
        var client = ClientWithAllowlist("https://app.example.com");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "https://evil.example.com");
        var resp = await client.SendAsync(req);

        // Python emits NO Access-Control-Allow-Origin at all when allowlist
        // is non-empty and origin doesn't match — so the browser blocks.
        resp.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        resp.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    // ==================== 5. OPTIONS preflight short-circuit ====================

    [Fact]
    public async Task Options_Preflight_Returns_204_With_Cors_Headers()
    {
        var client = ClientWithAllowlist("https://app.example.com");
        var req = new HttpRequestMessage(HttpMethod.Options, "/managed/request");
        req.Headers.Add("Origin", "https://app.example.com");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        var resp = await client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resp.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods).ShouldBeTrue();
        string.Join(",", methods!).ShouldContain("POST");
        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).ShouldBeTrue();
        string.Join(",", origins!).ShouldBe("https://app.example.com");
    }

    [Fact]
    public async Task Options_Preflight_Does_Not_Require_Auth()
    {
        // Preflight is unauthenticated by design — the middleware short-circuits
        // before UseAuthentication, so no JWT is required even on routes that
        // otherwise do. Using /managed/request which normally requires
        // authorization.
        var client = ClientWithAllowlist("");
        var req = new HttpRequestMessage(HttpMethod.Options, "/managed/request");
        req.Headers.Add("Origin", "http://127.0.0.1:5099");
        var resp = await client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resp.Headers.Contains("Access-Control-Allow-Origin").ShouldBeTrue();
    }
}
