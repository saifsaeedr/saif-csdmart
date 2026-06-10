using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// A refresh token must never work as an access token on the HTTP API.
//
// JwtIssuer labels every token with token_use=access|refresh. The OAuth
// refresh grant already rejects access-as-refresh; these tests pin the
// reverse direction. Session binding incidentally blocks refresh-as-access
// for web users (the sessions row stores the access token), but BOT users
// skip session checks entirely — so the bot case is the one that would leak
// without explicit token_use enforcement in JwtBearerSetup.OnTokenValidated.
public class TokenUseEnforcementTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public TokenUseEnforcementTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Bot_Refresh_Token_As_Bearer_Returns_401_Invalid_Token()
    {
        var user = await _factory.CreateLoggedInUserAsync(UserType.Bot);
        try
        {
            var jwt = _factory.Services.GetRequiredService<JwtIssuer>();
            var refresh = jwt.IssueRefresh(user.Shortname, UserType.Bot);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", refresh);
            // /info/settings is RequireAuthorization()-gated (no AllowAnonymous
            // override), so the JwtBearer challenge fires and returns the
            // canonical INVALID_TOKEN body.
            var resp = await client.GetAsync("/info/settings");

            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = JsonSerializer.Deserialize(
                await resp.Content.ReadAsStringAsync(),
                Dmart.Models.Json.DmartJsonContext.Default.Response);
            body!.Error!.Code.ShouldBe(InternalErrorCode.INVALID_TOKEN);
        }
        finally
        {
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Bot_Access_Token_As_Bearer_Still_Works()
    {
        var user = await _factory.CreateLoggedInUserAsync(UserType.Bot);
        try
        {
            var resp = await user.Client.GetAsync("/user/profile");
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await user.Cleanup();
        }
    }

    // ---- Legacy (claimless) token handling -------------------------------
    //
    // Tokens minted before the 2026-06 hardening carry no token_use claim.
    // Python dmart (EOL) never wrote it either. By DEFAULT such tokens are
    // still accepted (and counted via LegacyTokenMonitor) so the installed
    // base ages out gracefully; with JwtRequireTokenUse=true they are
    // rejected at every gate.

    [FactIfPg]
    public async Task Claimless_Bearer_Accepted_For_Bot_By_Default_And_Recorded()
    {
        var user = await _factory.CreateLoggedInUserAsync(UserType.Bot);
        try
        {
            var claimless = MintClaimlessToken(_factory, user.Shortname);
            var monitor = _factory.Services.GetRequiredService<LegacyTokenMonitor>();
            var before = monitor.Count;

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", claimless);
            var resp = await client.GetAsync("/user/profile");

            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            // >= because other tolerant-path tests may record concurrently.
            monitor.Count.ShouldBeGreaterThanOrEqualTo(before + 1);
        }
        finally
        {
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Claimless_Bearer_Returns_401_When_Strict()
    {
        var strict = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s => s.JwtRequireTokenUse = true)));
        var user = await _factory.CreateLoggedInUserAsync(host: strict, UserType.Bot);
        try
        {
            var claimless = MintClaimlessToken(strict, user.Shortname);
            var client = strict.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", claimless);
            var resp = await client.GetAsync("/info/settings");

            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = JsonSerializer.Deserialize(
                await resp.Content.ReadAsStringAsync(),
                Dmart.Models.Json.DmartJsonContext.Default.Response);
            body!.Error!.Code.ShouldBe(InternalErrorCode.INVALID_TOKEN);
        }
        finally
        {
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Claimless_Refresh_Token_Accepted_By_Default()
    {
        var user = await _factory.CreateLoggedInUserAsync(UserType.Web);
        try
        {
            var claimless = MintClaimlessToken(_factory, user.Shortname);
            var client = _factory.CreateClient();
            var resp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = claimless,
                }));

            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await resp.Content.ReadAsStringAsync()).ShouldContain("access_token");
        }
        finally
        {
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Claimless_Refresh_Token_Rejected_When_Strict()
    {
        var strict = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s => s.JwtRequireTokenUse = true)));
        var user = await _factory.CreateLoggedInUserAsync(host: strict, UserType.Web);
        try
        {
            var claimless = MintClaimlessToken(strict, user.Shortname);
            var client = strict.CreateClient();
            var resp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = claimless,
                }));

            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await resp.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
        }
        finally
        {
            await user.Cleanup();
        }
    }

    // Hand-rolled HS256 token signed with the host's configured JwtSecret but
    // WITHOUT token_use — simulates a token minted before the 2026-06
    // hardening (or by the EOL Python implementation).
    private static string MintClaimlessToken(
        WebApplicationFactory<Program> host, string subject)
    {
        var secret = host.Services
            .GetRequiredService<IOptions<DmartSettings>>().Value.JwtSecret;
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddMinutes(5).ToUnixTimeSeconds();
        var payload = $$"""
            {"sub":"{{subject}}","iss":"dmart","aud":"dmart","iat":{{now.ToUnixTimeSeconds()}},"exp":{{exp}},"data":{"shortname":"{{subject}}","type":"web"},"expires":{{exp}}}
            """;
        const string headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var header = JwtIssuer.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(headerJson));
        var body = JwtIssuer.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payload.Trim()));
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var sig = JwtIssuer.Base64UrlEncode(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{header}.{body}")));
        return $"{header}.{body}.{sig}";
    }

    [FactIfPg]
    public async Task Web_Refresh_Token_As_Bearer_Returns_401()
    {
        // Web users were already protected via session binding; pin it anyway
        // so a future session-check change can't silently reopen this path.
        var user = await _factory.CreateLoggedInUserAsync(UserType.Web);
        try
        {
            var jwt = _factory.Services.GetRequiredService<JwtIssuer>();
            var refresh = jwt.IssueRefresh(user.Shortname, UserType.Web);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", refresh);
            var resp = await client.GetAsync("/info/settings");

            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await user.Cleanup();
        }
    }
}
