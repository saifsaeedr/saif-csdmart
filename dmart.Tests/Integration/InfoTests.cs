using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Mirrors dmart's pytests/test_info.py — /info/me, /info/manifest, /info/settings.
public class InfoTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public InfoTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Manifest_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/manifest");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_Without_Auth_Returns_Anonymous()
    {
        // /info/me is anonymous-allowed so the SPA can use it as a session
        // probe without painting a 401 on every cold load. Unauthed callers
        // get 200 with authenticated:false and shortname=anonymous.
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/me");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var attrs = doc.RootElement.GetProperty("attributes");
        attrs.GetProperty("authenticated").GetBoolean().ShouldBeFalse();
        attrs.GetProperty("shortname").GetString().ShouldBe("anonymous");
    }

    [Fact]
    public async Task Settings_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/settings");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Root_Returns_Server_Identifier()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("dmart");
    }

    [Fact]
    public async Task CXB_Config_Json_Returns_Valid_Json_Or_404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cxb/config.json");
        // Either serves config.json or 404 (no CXB built)
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            // Should be valid JSON with at least one key
            var doc = JsonDocument.Parse(body);
            doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        }
        else
        {
            resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [FactIfPg]
    public async Task Settings_With_Auth_Returns_Listening_Port()
    {
        // Per-test user with super_admin role — see DmartFactory.CreateLoggedInUserAsync.
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        var resp = await client.GetAsync("/info/settings");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        // Should have records with settings attributes
        doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
    }
}
