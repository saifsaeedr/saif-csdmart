using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Verifies the AUTH_RATE_LIMIT_PER_MINUTE setting (e4eb47c) is actually
// wired into the per-IP fixed-window limiter at Program.cs:879. With the
// knob set to 3, the 4th hit of /user/login from the same partition must
// return HTTP 429 regardless of credential validity — the limiter runs
// BEFORE authentication, so bogus creds still consume a token.
//
// This test uses its own WebApplicationFactory (not the shared DmartFactory
// which is sealed) so we can set a low limit without affecting the other
// test classes. Each WAF instance gets its own Kestrel + DI + rate-limiter
// state, so partition budgets don't cross-contaminate.
public sealed class AuthRateLimitTests : IClassFixture<AuthRateLimitTests.LowRateLimitFactory>
{
    private readonly LowRateLimitFactory _factory;
    public AuthRateLimitTests(LowRateLimitFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Login_Exceeding_AuthRateLimitPerMinute_Returns_429()
    {
        using var client = _factory.CreateClient();
        // Bogus creds on purpose — we're asserting on the rate-limiter
        // envelope, not the auth envelope. First 3 attempts will 401; the
        // 4th must 429 regardless of auth outcome.
        var body = new UserLoginRequest("rate_limit_ghost_8f3a", null, null, "wrong-pw", null);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await client.PostAsJsonAsync(
                "/user/login", body, DmartJsonContext.Default.UserLoginRequest);
        }

        last.ShouldNotBeNull();
        ((int)last.StatusCode).ShouldBe(429);

        // OnRejected emits a small JSON envelope matching Response.Fail shape
        // (Program.cs:895-902) — clients keying off error.type/code see the
        // same structure they see for other failure modes.
        var payload = await last.Content.ReadAsStringAsync();
        payload.ShouldContain("\"status\":\"failed\"");
        payload.ShouldContain("\"type\":\"rate_limit\"");
        payload.ShouldContain("\"code\":429");
    }

    // Custom factory with AuthRateLimitPerMinute=3 overlaid on top of the
    // standard test-host config. Mirrors the shape in DmartFactory /
    // LogFileTests so the DB-gating (PgConn null-out) still works.
    public sealed class LowRateLimitFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes",
                    ["Dmart:JwtIssuer"] = "dmart",
                    ["Dmart:JwtAudience"] = "dmart",
                    ["Dmart:JwtAccessExpires"] = "300",
                    ["Dmart:AdminPassword"] = "admin-password-123",
                    ["Dmart:AdminEmail"] = "admin@test.local",
                    // The one knob under test.
                    ["Dmart:AuthRateLimitPerMinute"] = "3",
                };
                if (!string.IsNullOrEmpty(DmartFactory.PgConn))
                {
                    overrides["Dmart:PostgresConnection"] = DmartFactory.PgConn;
                    overrides["Dmart:DatabaseHost"] = null;
                    overrides["Dmart:DatabasePassword"] = null;
                    overrides["Dmart:DatabaseName"] = null;
                }
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
