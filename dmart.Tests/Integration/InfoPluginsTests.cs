using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end coverage for GET /info/plugins. Pinned points:
//   - Auth required (mirrors /info/manifest's gate).
//   - Authenticated response carries records, each with shortname, version, type.
//   - /info/manifest's plugins array still ships the legacy string-of-shortnames
//     shape so existing consumers don't break (back-compat assertion).
public class InfoPluginsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public InfoPluginsTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Plugins_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/plugins");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [FactIfPg]
    public async Task Plugins_With_Auth_Returns_Records_With_Version_And_Type()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        var resp = await client.GetAsync("/info/plugins");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().ShouldBe("success");

        var records = doc.RootElement.GetProperty("records");
        records.ValueKind.ShouldBe(JsonValueKind.Array);
        // dmart ships with built-in plugins, so the active list should be
        // non-empty in any normal test environment.
        records.GetArrayLength().ShouldBeGreaterThan(0);

        foreach (var rec in records.EnumerateArray())
        {
            rec.GetProperty("resource_type").GetString().ShouldBe("plugin_wrapper");
            rec.GetProperty("shortname").GetString().ShouldNotBeNullOrEmpty();

            var attrs = rec.GetProperty("attributes");
            var version = attrs.GetProperty("version").GetString();
            version.ShouldNotBeNullOrEmpty();

            var type = attrs.GetProperty("type").GetString();
            type.ShouldBeOneOf("hook", "api");
        }
    }

    [FactIfPg]
    public async Task Manifest_Plugins_Array_Still_Returns_Shortnames_Only()
    {
        // Back-compat assertion: any client that already consumes
        // /info/manifest's plugins array as a list of strings must keep
        // working after this change. /info/plugins is the new home for the
        // richer shape.
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        var resp = await client.GetAsync("/info/manifest");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var plugins = doc.RootElement.GetProperty("attributes").GetProperty("plugins");
        plugins.ValueKind.ShouldBe(JsonValueKind.Array);
        foreach (var item in plugins.EnumerateArray())
        {
            // Each entry is a plain string shortname — NOT an object.
            item.ValueKind.ShouldBe(JsonValueKind.String);
        }
    }
}
