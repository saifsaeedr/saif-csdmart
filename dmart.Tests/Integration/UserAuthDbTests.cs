using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// DB-backed integration tests. Skip cleanly when DMART_TEST_PG_CONN is unset so the
// suite can still run on a developer machine without PostgreSQL.
public class UserAuthDbTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserAuthDbTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Bootstrap_Admin_Can_Login()
    {
        if (!DmartFactory.HasPg) return;  // skipped — no DB

        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body.ShouldNotBeNull();
        body!.Status.ShouldBe(Status.Success);
        body.Attributes.ShouldNotBeNull();
        body.Attributes!.ContainsKey("access_token").ShouldBeTrue();
    }

    [Fact]
    public async Task Wrong_Password_Returns_401()
    {
        if (!DmartFactory.HasPg) return;

        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, "definitely-wrong", null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_Me_Returns_Identity()
    {
        if (!DmartFactory.HasPg) return;

        var client = _factory.CreateClient();
        var token = await LoginAdminAndGetTokenAsync(client);
        token.ShouldNotBeNull();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.GetAsync("/info/me");
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var wwwAuth = string.Join("; ", resp.Headers.WwwAuthenticate.Select(h => h.ToString()));
            throw new Xunit.Sdk.XunitException($"expected OK, got {resp.StatusCode}; WWW-Authenticate: {wwwAuth}");
        }
    }

    [Fact]
    public async Task Profile_Returns_Admin_Details_When_Authenticated()
    {
        if (!DmartFactory.HasPg) return;

        var client = _factory.CreateClient();
        var token = await LoginAdminAndGetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/user/profile");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        body.Attributes!["shortname"]!.ToString().ShouldBe(_factory.AdminShortname);
    }

    [Fact]
    public async Task Check_Existing_Returns_True_For_Admin()
    {
        if (!DmartFactory.HasPg) return;

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/user/check-existing?shortname={_factory.AdminShortname}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        // Response.Attributes is Dictionary<string, object>; source-gen JSON deserializes
        // values as JsonElement. Compare via the JsonElement's value kind.
        // Python-parity: per-field {shortname: bool, email: bool, msisdn: bool}.
        var snExists = (JsonElement)body!.Attributes!["shortname"]!;
        snExists.GetBoolean().ShouldBeTrue();
    }

    private async Task<string?> LoginAdminAndGetTokenAsync(HttpClient client)
    {
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return body?.Attributes?["access_token"]?.ToString();
    }
}
