using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Dmart.DataAdapters.Sql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the contract for the now-public short-link resolver
// (Api/Managed/ShortLinkHandler.cs:21 — `.AllowAnonymous()`):
//   * anonymous GETs return 302 for valid tokens
//   * off-host stored URLs return 404 (open-redirect guard in
//     Services/ShortLinkService.cs::ResolveAsync)
//   * the resolver shares the auth-by-ip rate limiter so a flood of
//     unauthenticated requests is capped (Program.cs:1126 policy).
//
// Two factories: a normal-rate fixture for the 302/404 cases (so the
// shared partition isn't pre-exhausted by another test in the class) and
// a tight-rate fixture used by the rate-limit case alone. Both pin AppUrl
// so the host-check test can seed a deliberately off-host URL.
public sealed class ShortLinkAnonymousResolveTests
    : IClassFixture<ShortLinkAnonymousResolveTests.OnHostFactory>,
      IClassFixture<ShortLinkAnonymousResolveTests.RateLimitFactory>
{
    private readonly OnHostFactory _onHost;
    private readonly RateLimitFactory _rl;

    public ShortLinkAnonymousResolveTests(OnHostFactory onHost, RateLimitFactory rl)
    {
        _onHost = onHost;
        _rl = rl;
    }

    private const string AppUrl = "http://localhost:8282";

    [FactIfPg]
    public async Task AnonymousGet_OnHostStoredUrl_Returns_302()
    {
        var target = $"{AppUrl}/managed/entry/content/management/users/dmart";

        // Use CreateAsync (the auto-token form) instead of CreateWithTokenAsync —
        // the latter's ON CONFLICT (token_uuid) clause requires a unique index
        // that the production schema doesn't have, so it errors out.
        var links = _onHost.Services.GetRequiredService<LinkRepository>();
        var token = await links.CreateAsync(target);

        using var client = _onHost.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        // Critically: NO Authorization header — proves AllowAnonymous() works.
        var resp = await client.GetAsync($"/managed/s/{token}");
        resp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().ShouldBe(target);
    }

    [FactIfPg]
    public async Task AnonymousGet_OffHostStoredUrl_Returns_404()
    {
        // Stored URL points at a different host than AppUrl: the resolver's
        // host-check should refuse rather than emit an open redirect.
        var hostile = "https://evil.example.com/landing";

        var links = _onHost.Services.GetRequiredService<LinkRepository>();
        var token = await links.CreateAsync(hostile);

        using var client = _onHost.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var resp = await client.GetAsync($"/managed/s/{token}");
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [FactIfPg]
    public async Task AnonymousGet_FloodingResolver_Hits_RateLimit_429()
    {
        // RateLimitFactory pins AuthRateLimitPerMinute=3, so the 4th hit
        // from the same partition must 429 even when the token resolves
        // cleanly. Isolated factory keeps the partition fresh.
        var target = $"{AppUrl}/managed/entry/content/management/users/dmart";

        var links = _rl.Services.GetRequiredService<LinkRepository>();
        var token = await links.CreateAsync(target);

        using var client = _rl.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await client.GetAsync($"/managed/s/{token}");
        }
        last.ShouldNotBeNull();
        ((int)last.StatusCode).ShouldBe(429);

        var payload = await last.Content.ReadAsStringAsync();
        payload.ShouldContain("\"status\":\"failed\"");
        payload.ShouldContain("\"type\":\"rate_limit\"");
        payload.ShouldContain("\"code\":429");
    }

    private static void ApplyCommonOverrides(IDictionary<string, string?> overrides, int authRateLimit)
    {
        overrides["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes";
        overrides["Dmart:JwtIssuer"] = "dmart";
        overrides["Dmart:JwtAudience"] = "dmart";
        overrides["Dmart:JwtAccessExpires"] = "300";
        overrides["Dmart:AdminPassword"] = "admin-password-123";
        overrides["Dmart:AdminEmail"] = "admin@test.local";
        overrides["Dmart:AppUrl"] = AppUrl;
        overrides["Dmart:AuthRateLimitPerMinute"] = authRateLimit.ToString();
        if (!string.IsNullOrEmpty(DmartFactory.PgConn))
        {
            overrides["Dmart:PostgresConnection"] = DmartFactory.PgConn;
            overrides["Dmart:DatabaseHost"] = null;
            overrides["Dmart:DatabasePassword"] = null;
            overrides["Dmart:DatabaseName"] = null;
        }
    }

    public sealed class OnHostFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var overrides = new Dictionary<string, string?>();
                ApplyCommonOverrides(overrides, authRateLimit: 60);
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }

    public sealed class RateLimitFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var overrides = new Dictionary<string, string?>();
                ApplyCommonOverrides(overrides, authRateLimit: 3);
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
