using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the /managed/request create response shape — every resource type must
// echo created_at, updated_at, owner_shortname on the response record, matching
// Python's resource_obj.to_record(subpath, shortname, []) output (core.py:296).
public class ManagedRequestCreateResponseTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedRequestCreateResponseTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task User_Create_Response_Includes_Created_Updated_Owner()
    {
        // Per-test user with super_admin role so the request authenticates and
        // the new strict OnTokenValidated session check accepts the token.
        var (client, _, actor, _) = await _factory.CreateLoggedInUserAsync();
        var shortname = "u_" + Guid.NewGuid().ToString("N")[..8];
        var body = "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/users\"," +
            "\"attributes\":{\"email\":\"" + shortname + "@x.y\",\"password\":\"Testtest1234\"}}]}";
        var resp = await client.PostAsync("/managed/request",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = JsonSerializer.Deserialize(
            await resp.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response);

        result!.Status.ShouldBe(Status.Success);
        var attrs = result.Records!.Single().Attributes!;
        attrs.ShouldContainKey("created_at");
        attrs.ShouldContainKey("updated_at");
        attrs.ShouldContainKey("owner_shortname");
        attrs["owner_shortname"].ToString().ShouldBe(actor);

        var users = _factory.Services.GetRequiredService<UserRepository>();
        await users.DeleteAsync(shortname);
    }

    [FactIfPg]
    public async Task Role_Create_Response_Includes_Created_Updated_Owner()
    {
        var (client, _, actor, _) = await _factory.CreateLoggedInUserAsync();
        var shortname = "r_" + Guid.NewGuid().ToString("N")[..8];
        var body = "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"role\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/roles\"," +
            "\"attributes\":{\"permissions\":[]}}]}";
        var resp = await client.PostAsync("/managed/request",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = JsonSerializer.Deserialize(
            await resp.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response);

        result!.Status.ShouldBe(Status.Success);
        var attrs = result.Records!.Single().Attributes!;
        attrs.ShouldContainKey("created_at");
        attrs.ShouldContainKey("updated_at");
        attrs.ShouldContainKey("owner_shortname");
        attrs["owner_shortname"].ToString().ShouldBe(actor);

        var access = _factory.Services.GetRequiredService<AccessRepository>();
        await access.DeleteRoleAsync(shortname);
    }

    // Regression for the create→fetch ACL round-trip. The bug was two-fold:
    // (1) RequestHandler.MaterializeEntry ignored the "acl" attribute, so
    // Entry.Acl persisted as null, and (2) the Dmart AclEntry field was named
    // `Allowed` (→ JSON "allowed") instead of the Python-compatible
    // `AllowedActions` (→ "allowed_actions"). This test drives the exact shape
    // a client would send, via the /managed/entry fetch endpoint that reads
    // from the DB, bypassing the HTTP /managed/request permission layer (which
    // is orthogonal to the ACL persistence logic).
    [FactIfPg]
    public async Task Create_Entry_With_Acl_Persists_And_Fetch_Returns_It()
    {
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var entryRepo = _factory.Services.GetRequiredService<EntryRepository>();
        var spaceName = "aclt_" + Guid.NewGuid().ToString("N")[..6];

        await spaces.UpsertAsync(new Models.Core.Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = _factory.AdminShortname,
            IsActive = true,
            Languages = new() { Language.En },
            ActivePlugins = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            // Drive MaterializeEntry directly — same path /managed/request takes,
            // without the permission-gate and auth layers on top.
            var shortname = "e_" + Guid.NewGuid().ToString("N")[..8];
            var attrsJson = "{\"is_active\":true,\"acl\":[" +
                "{\"user_shortname\":\"ziq03886\",\"allowed_actions\":[\"view\",\"query\"]}," +
                "{\"user_shortname\":\"ziq03294\",\"allowed_actions\":[\"update\",\"view\"]}]}";
            var attrsDoc = JsonDocument.Parse(attrsJson);
            var attrs = new Dictionary<string, object>();
            foreach (var prop in attrsDoc.RootElement.EnumerateObject())
                attrs[prop.Name] = prop.Value.Clone();
            var rec = new Models.Api.Record
            {
                ResourceType = ResourceType.Content,
                Shortname = shortname,
                Subpath = "/",
                Attributes = attrs,
            };
            var entry = Api.Managed.RequestHandler.MaterializeEntry(rec, spaceName, _factory.AdminShortname);

            entry.Acl.ShouldNotBeNull();
            entry.Acl!.Count.ShouldBe(2);
            entry.Acl[0].UserShortname.ShouldBe("ziq03886");
            entry.Acl[0].AllowedActions.ShouldNotBeNull();
            entry.Acl[0].AllowedActions!.ShouldBe(new List<string> { "view", "query" });

            // Round-trip through the DB to make sure serialization preserves
            // the JSON field names. Re-read via the same repo the fetch handler
            // uses.
            await entryRepo.UpsertAsync(entry);
            var loaded = await entryRepo.GetAsync(spaceName, "/", shortname, ResourceType.Content);
            loaded.ShouldNotBeNull();
            loaded!.Acl.ShouldNotBeNull();
            loaded.Acl!.Count.ShouldBe(2);
            loaded.Acl[0].AllowedActions!.ShouldBe(new List<string> { "view", "query" });

            // Serialized JSON shape — what /managed/entry returns — must use
            // `allowed_actions` (not `allowed`), matching Python clients.
            var json = JsonSerializer.Serialize(loaded, DmartJsonContext.Default.Entry);
            using var doc = JsonDocument.Parse(json);
            var aclEl = doc.RootElement.GetProperty("acl");
            aclEl.ValueKind.ShouldBe(JsonValueKind.Array);
            aclEl.GetArrayLength().ShouldBe(2);
            aclEl[0].GetProperty("user_shortname").GetString().ShouldBe("ziq03886");
            aclEl[0].TryGetProperty("allowed_actions", out _).ShouldBeTrue("must serialize as allowed_actions");
            aclEl[0].TryGetProperty("allowed", out _).ShouldBeFalse("legacy `allowed` name must not leak");
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }
}
