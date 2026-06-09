using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
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
