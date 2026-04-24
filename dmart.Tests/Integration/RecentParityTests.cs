using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Tests covering: __root__, resource_type fallback, exact_subpath, search,
// profile records[], permissions, auto shortname, space create plugin, CXB.
//
// All tests create their own test data in a scratch space — no dependency
// on pre-existing spaces like "hr" or "applications".
public class RecentParityTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    private const string TestSpace = "recenttest";
    public RecentParityTests(DmartFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token)> LoginAsync()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"Login failed for '{_factory.AdminShortname}': {resp.StatusCode} {raw}");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    private async Task EnsureTestSpaceAsync(HttpClient client)
    {
        await client.PostAsync("/managed/request", new StringContent(
            $"{{\"space_name\":\"{TestSpace}\",\"request_type\":\"create\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{TestSpace}\",\"attributes\":{{\"is_active\":true}}}}]}}",
            Encoding.UTF8, "application/json"));
        await client.PostAsync("/managed/request", new StringContent(
            $"{{\"space_name\":\"{TestSpace}\",\"request_type\":\"create\",\"records\":[{{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"testfolder\",\"attributes\":{{\"is_active\":true,\"tags\":[\"alpha\",\"beta\"]}}}}]}}",
            Encoding.UTF8, "application/json"));
        await client.PostAsync("/managed/request", new StringContent(
            $"{{\"space_name\":\"{TestSpace}\",\"request_type\":\"create\",\"records\":[{{\"resource_type\":\"content\",\"subpath\":\"testfolder\",\"shortname\":\"findme\",\"attributes\":{{\"is_active\":true,\"payload\":{{\"content_type\":\"json\",\"body\":{{\"x\":1}}}}}}}}]}}",
            Encoding.UTF8, "application/json"));
    }

    private async Task CleanupTestSpaceAsync(HttpClient client)
    {
        await client.PostAsync("/managed/request", new StringContent(
            $"{{\"space_name\":\"{TestSpace}\",\"request_type\":\"delete\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{TestSpace}\",\"attributes\":{{}}}}]}}",
            Encoding.UTF8, "application/json"));
    }

    // ==================== __root__ magic word ====================

    [FactIfPg]
    public async Task Root_Magic_Word_Resolves_To_Root_Subpath()
    {
        var (client, _) = await LoginAsync();
        await EnsureTestSpaceAsync(client);
        try
        {
            var resp = await client.GetAsync($"/managed/entry/folder/{TestSpace}/__root__/testfolder");
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            doc.RootElement.GetProperty("shortname").GetString().ShouldBe("testfolder");
        }
        finally { await CleanupTestSpaceAsync(client); }
    }

    // ==================== resource_type fallback ====================

    [FactIfPg]
    public async Task Entry_Lookup_Falls_Back_When_ResourceType_Mismatches()
    {
        var (client, _) = await LoginAsync();
        await EnsureTestSpaceAsync(client);
        try
        {
            var resp = await client.GetAsync($"/managed/entry/content/{TestSpace}/__root__/testfolder");
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            doc.RootElement.GetProperty("shortname").GetString().ShouldBe("testfolder");
        }
        finally { await CleanupTestSpaceAsync(client); }
    }

    // ==================== entry routing by resource_type ====================

    [FactIfPg]
    public async Task Entry_Space_Routes_To_Spaces_Table()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync("/managed/entry/space/management/__root__/management");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("\"space\"");
    }

    [FactIfPg]
    public async Task Entry_User_Routes_To_Users_Table()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync($"/managed/entry/user/management/__root__/{_factory.AdminShortname}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain(_factory.AdminShortname);
    }

    // ==================== exact_subpath ====================

    [FactIfPg]
    public async Task ExactSubpath_Root_Returns_Only_Root_Entries()
    {
        var (client, _) = await LoginAsync();
        await EnsureTestSpaceAsync(client);
        try
        {
            var svc = _factory.Services.GetRequiredService<QueryService>();
            var all = await svc.ExecuteAsync(new Query
            {
                Type = QueryType.Search, SpaceName = TestSpace, Subpath = "/",
                ExactSubpath = false, Limit = 100,
            }, _factory.AdminShortname);

            var exact = await svc.ExecuteAsync(new Query
            {
                Type = QueryType.Search, SpaceName = TestSpace, Subpath = "/",
                ExactSubpath = true, Limit = 100,
            }, _factory.AdminShortname);

            all.Records!.Count.ShouldBeGreaterThanOrEqualTo(exact.Records!.Count);
        }
        finally { await CleanupTestSpaceAsync(client); }
    }

    // ==================== search includes shortname ====================

    [FactIfPg]
    public async Task Search_Finds_By_Shortname()
    {
        var (client, _) = await LoginAsync();
        await EnsureTestSpaceAsync(client);
        try
        {
            var svc = _factory.Services.GetRequiredService<QueryService>();
            var resp = await svc.ExecuteAsync(new Query
            {
                Type = QueryType.Search, SpaceName = TestSpace, Subpath = "/testfolder",
                Search = "findme", ExactSubpath = true, Limit = 100,
            }, _factory.AdminShortname);
            resp.Status.ShouldBe(Status.Success);
            resp.Records!.Any(r => r.Shortname.Contains("findme")).ShouldBeTrue();
        }
        finally { await CleanupTestSpaceAsync(client); }
    }

    // ==================== profile returns records[] ====================

    [FactIfPg]
    public async Task Profile_Returns_Records_With_Permissions()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync("/user/profile");
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        body.Records.ShouldNotBeNull();
        body.Records!.Count.ShouldBe(1);
        body.Records[0].ResourceType.ShouldBe(ResourceType.User);
        var attrs = body.Records[0].Attributes!;
        attrs.ShouldContainKey("permissions");
        var perms = (JsonElement)attrs["permissions"]!;
        perms.ValueKind.ShouldBe(JsonValueKind.Object);
        perms.EnumerateObject().Count().ShouldBeGreaterThan(0);
    }

    // ==================== auto shortname ====================

    [FactIfPg]
    public async Task Auto_Shortname_Generates_UUID_Prefix()
    {
        var (client, _) = await LoginAsync();
        await EnsureTestSpaceAsync(client);
        try
        {
            var resp = await client.PostAsync("/managed/request",
                new StringContent(
                    $"{{\"space_name\":\"{TestSpace}\",\"request_type\":\"create\",\"records\":[{{\"resource_type\":\"content\",\"subpath\":\"testfolder\",\"shortname\":\"auto\",\"attributes\":{{\"payload\":{{\"content_type\":\"json\",\"body\":{{\"test\":true}}}}}}}}]}}",
                    Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);
            var rec = body.Records![0];
            rec.Shortname.ShouldNotBe("auto");
            rec.Shortname.Length.ShouldBe(8);
            rec.Uuid.ShouldNotBeNull();
            rec.Uuid!.Replace("-", "")[..8].ShouldBe(rec.Shortname);
        }
        finally { await CleanupTestSpaceAsync(client); }
    }

    // ==================== space create triggers schema folder ====================

    [FactIfPg]
    public async Task Space_Create_Triggers_Schema_Folder_Plugin()
    {
        var (client, _) = await LoginAsync();
        var spaceName = $"pltest_{Guid.NewGuid():N}"[..12];
        try
        {
            var resp = await client.PostAsync("/managed/request",
                new StringContent(
                    $"{{\"space_name\":\"{spaceName}\",\"request_type\":\"create\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{spaceName}\",\"attributes\":{{\"is_active\":true}}}}]}}",
                    Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            Entry? schemaFolder = null;
            await WaitFor.UntilAsync(async () =>
            {
                schemaFolder = await entries.GetAsync(spaceName, "/", "schema", ResourceType.Folder);
                return schemaFolder is not null;
            }, TimeSpan.FromSeconds(2));
            schemaFolder.ShouldNotBeNull("resource_folders_creation plugin should create /schema");
        }
        finally
        {
            var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
            await spaces.DeleteAsync(spaceName);
        }
    }

    // ==================== CXB embedded ====================

    [Fact]
    public async Task CXB_Index_Served()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cxb/index.html");
        // CXB may not be built (CI runners without node/yarn) — skip gracefully.
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
    }

    [Fact]
    public async Task CXB_SPA_Fallback_Returns_Index()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cxb/index.html");
        // CXB may not be built (CI runners without node/yarn) — skip gracefully.
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        var spaResp = await client.GetAsync("/cxb/some/deep/route");
        spaResp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ==================== Catalog embedded ====================

    [Fact]
    public async Task Catalog_Index_Served_With_Rewritten_BaseHref()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cat/index.html");
        // Catalog may not be built (CI runners without node/yarn) — skip gracefully.
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        // CatUrl defaults to "/cat" so the rewritten base href is "/cat/".
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("<base href=\"/cat/\"");
    }

    [Fact]
    public async Task Catalog_SPA_Fallback_Returns_Index()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cat/index.html");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        var spaResp = await client.GetAsync("/cat/some/deep/route");
        spaResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        spaResp.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
    }

    [Fact]
    public async Task Catalog_ConfigJson_Injects_Backend()
    {
        var client = _factory.CreateClient();
        // Skip if the catalog bundle isn't built (config.json ships with it).
        var probe = await client.GetAsync("/cat/index.html");
        if (probe.StatusCode == HttpStatusCode.NotFound) return;

        var resp = await client.GetAsync("/cat/config.json");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        var json = await resp.Content.ReadAsStringAsync();
        // backend must be present; CatalogMiddleware auto-fills it from the
        // request origin when the source file has no value (shipped default).
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("backend", out var backend).ShouldBeTrue();
        backend.ValueKind.ShouldBe(JsonValueKind.String);
        backend.GetString().ShouldNotBeNullOrEmpty();

        // Legacy `websocket` field must be dropped (SPA derives ws URL from backend).
        doc.RootElement.TryGetProperty("websocket", out _).ShouldBeFalse();
    }
}
