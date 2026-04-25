using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// HTTP-level end-to-end tests for /public/query as anonymous. Pins the full
// request→permission→SQL→response chain that repository- and service-level
// tests don't cover. Seeds a fresh permission + role + anonymous user + a
// few entries per run; restores any pre-existing anonymous/world rows on
// teardown so shared DBs stay intact.
[Collection(AnonymousWorldCollection.Name)]
public sealed class PublicQueryAnonymousTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PublicQueryAnonymousTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task PublicQuery_Anonymous_With_World_Permission_Returns_Sorted_Entries()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();

        const string anonUser = "anonymous";   // Python-reserved
        const string worldPerm = "world";      // Python-reserved
        var anonRole = $"itest_anon_role_{Guid.NewGuid():N}".Substring(0, 24);
        var space = $"itest_space_{Guid.NewGuid():N}".Substring(0, 24);
        var subpath = "/items";

        // Preserve any pre-existing anon/world rows so other tests aren't disturbed.
        var priorAnon = await users.GetByShortnameAsync(anonUser);
        var priorWorld = await access.GetPermissionAsync(worldPerm);

        // Three entries with distinct numeric ranks — numeric-aware sort must
        // put them in ASC order 1,2,10 (not alphabetic 1,10,2).
        var seeded = new[] { ("c", 10), ("a", 2), ("b", 1) };

        try
        {
            // World permission mirroring the field-reported shape:
            //   leading-slash subpath, conditions=[is_active], actions=[view,query].
            await access.UpsertPermissionAsync(new Permission
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = worldPerm,
                SpaceName = "management",
                Subpath = "permissions",
                OwnerShortname = "dmart",
                IsActive = true,
                Subpaths = new() { [space] = new() { "/items" } }, // leading slash
                ResourceTypes = new() { "content" },
                Actions = new() { "view", "query" },
                Conditions = new() { "is_active" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await access.UpsertRoleAsync(new Role
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = anonRole,
                SpaceName = "management",
                Subpath = "roles",
                OwnerShortname = "dmart",
                IsActive = true,
                Permissions = new() { worldPerm },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = anonUser,
                SpaceName = "management",
                Subpath = "users",
                OwnerShortname = anonUser,
                IsActive = true,
                Roles = new() { anonRole },
                Type = UserType.Web,
                Language = Language.En,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            foreach (var (shortname, rank) in seeded)
            {
                var payloadBody = JsonDocument.Parse($"{{\"rank\":{rank}}}").RootElement.Clone();
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = shortname,
                    SpaceName = space,
                    Subpath = subpath,
                    OwnerShortname = "dmart",
                    ResourceType = ResourceType.Content,
                    IsActive = true,
                    Payload = new Payload { ContentType = ContentType.Json, Body = payloadBody },
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            await access.InvalidateAllCachesAsync();

            // No Authorization header — we want the anonymous code path.
            var client = _factory.CreateClient();
            var reqBody = new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = subpath,
                FilterSchemaNames = new(),
                SortBy = "payload.body.rank",
                SortType = SortType.Ascending,
                Limit = 10,
                RetrieveJsonPayload = true,
                // /public/query defaults retrieve_total to false (skip the
                // parallel COUNT for anon traffic). This test pins the
                // total assertion below, so opt back in.
                RetrieveTotal = true,
            };
            var resp = await client.PostAsJsonAsync("/public/query", reqBody, DmartJsonContext.Default.Query);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var response = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            response.ShouldNotBeNull();
            response!.Status.ShouldBe(Status.Success);
            response.Records.ShouldNotBeNull();
            response.Records!.Count.ShouldBe(3, "anonymous must see all three seeded entries");

            var shortnames = response.Records.Select(r => r.Shortname).ToArray();
            // Ranks 1,2,10 → ASC must be b (1), a (2), c (10). Alphabetic sort
            // would give a,b,c (wrong for numeric) — this pins the CASE-numeric
            // branch in BuildJsonPathSortExpression.
            shortnames.ShouldBe(new[] { "b", "a", "c" });

            // Attributes carry total + returned. HTTP round-trip deserializes
            // the int into a JsonElement — cast through the JSON representation.
            response.Attributes.ShouldNotBeNull();
            ((JsonElement)response.Attributes!["total"]).GetInt32().ShouldBe(3);
            ((JsonElement)response.Attributes["returned"]).GetInt32().ShouldBe(3);
        }
        finally
        {
            foreach (var (shortname, _) in seeded)
                try { await entries.DeleteAsync(space, subpath, shortname, ResourceType.Content); } catch { }
            try { await users.DeleteAsync(anonUser); } catch { }
            try { await access.DeleteRoleAsync(anonRole); } catch { }
            try { await access.DeletePermissionAsync(worldPerm); } catch { }
            if (priorAnon is not null)  await users.UpsertAsync(priorAnon);
            if (priorWorld is not null) await access.UpsertPermissionAsync(priorWorld);
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task PublicQuery_Anonymous_Without_World_Returns_Zero()
    {
        // Contract: without a world permission in place, anonymous gets empty
        // results (not a 401). The route accepts the request; the permission
        // walk just yields no match → EmptyQueryResponse.
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();

        const string anonUser = "anonymous";
        const string worldPerm = "world";
        var priorAnon = await users.GetByShortnameAsync(anonUser);
        var priorWorld = await access.GetPermissionAsync(worldPerm);
        // Simulate the no-config state without deleting the user: on a dev
        // DB there are real entries.owner_shortname FK'd to "anonymous"
        // that would block the delete. Stripping roles/groups achieves the
        // same effective "no grants" state the test is asserting about.
        if (priorAnon is not null)
            await users.UpsertAsync(priorAnon with { Roles = new(), Groups = new() });
        if (priorWorld is not null) await access.DeletePermissionAsync(worldPerm);
        await access.InvalidateAllCachesAsync();

        try
        {
            var client = _factory.CreateClient();
            var reqBody = new Query
            {
                Type = QueryType.Search,
                SpaceName = "management",
                Subpath = "/users",
                Limit = 10,
            };
            var resp = await client.PostAsJsonAsync("/public/query", reqBody, DmartJsonContext.Default.Query);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var response = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            response!.Status.ShouldBe(Status.Success);
            ((JsonElement)response.Attributes!["total"]).GetInt32().ShouldBe(0);
            (response.Records?.Count ?? 0).ShouldBe(0);
        }
        finally
        {
            if (priorAnon is not null)  await users.UpsertAsync(priorAnon);
            if (priorWorld is not null) await access.UpsertPermissionAsync(priorWorld);
            await access.InvalidateAllCachesAsync();
        }
    }

    // ==================== World permission boundary coverage ====================
    //
    // Over-access: the permission walk MUST honor the magic words that widen
    // scope (__all_spaces__, __all_subpaths__).
    // Under-access: the walk MUST NOT grant when the scope doesn't cover the
    // request (wrong space, wrong subpath, resource type mismatch, missing
    // action, inactive permission, entries with is_active=false under the
    // is_active condition).

    // ---- over-access ----

    [FactIfPg]
    public async Task World_AllSpaces_Magic_Word_Grants_Any_Space()
    {
        // Permission keyed under "__all_spaces__": any space name on the wire
        // must resolve via that bucket. Uses a freshly-created space unrelated
        // to the permission's configured scope — proves the magic word works.
        await using var h = await WorldScopeHarness.CreateAsync(_factory,
            subpaths: new() { [PermissionService.AllSpacesMw] = new() { PermissionService.AllSubpathsMw } },
            actions: new() { "view", "query" },
            resourceTypes: new() { "content" },
            seedSpace: true,
            seeds: new[] { ("x", 1) });

        var resp = await h.Client.PostAsJsonAsync("/public/query",
            new Query { Type = QueryType.Search, SpaceName = h.Space, Subpath = h.Subpath,
                        FilterSchemaNames = new(), Limit = 10 },
            DmartJsonContext.Default.Query);
        var r = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        r!.Status.ShouldBe(Status.Success);
        r.Records!.Count.ShouldBe(1, "__all_spaces__ must permit any space");
    }

    [FactIfPg]
    public async Task World_AllSubpaths_Magic_Word_Grants_Any_Subpath_In_Space()
    {
        // Permission has the space scoped but subpath list is __all_subpaths__:
        // any subpath inside that space must pass. Seed at a subpath NOT
        // enumerated literally.
        await using var h = await WorldScopeHarness.CreateAsync(_factory,
            subpathsForSpace: new() { PermissionService.AllSubpathsMw },
            actions: new() { "view", "query" },
            resourceTypes: new() { "content" },
            seeds: new[] { ("x", 1) });

        var resp = await h.Client.PostAsJsonAsync("/public/query",
            new Query { Type = QueryType.Search, SpaceName = h.Space, Subpath = h.Subpath,
                        FilterSchemaNames = new(), Limit = 10 },
            DmartJsonContext.Default.Query);
        var r = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        r!.Records!.Count.ShouldBe(1, "__all_subpaths__ must grant under any subpath in the space");
    }

    // ---- under-access ----

    [FactIfPg]
    public async Task World_Scoped_To_Different_Space_Denies_Other_Space()
    {
        // Permission lists space A; probe space B (the seed space) — must
        // return zero. The caller's anonymous user has ONLY this permission,
        // so B is outside its scope.
        var otherSpace = $"other_space_{Guid.NewGuid():N}"[..16];
        await using var h = await WorldScopeHarness.CreateAsync(_factory,
            subpaths: new() { [otherSpace] = new() { PermissionService.AllSubpathsMw } },
            actions: new() { "view", "query" },
            resourceTypes: new() { "content" },
            seeds: new[] { ("x", 1) });

        var resp = await h.Client.PostAsJsonAsync("/public/query",
            new Query { Type = QueryType.Search, SpaceName = h.Space, Subpath = h.Subpath,
                        FilterSchemaNames = new(), Limit = 10 },
            DmartJsonContext.Default.Query);
        var r = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        (r!.Records?.Count ?? 0).ShouldBe(0, "permission scoped to a different space must NOT grant this one");
    }

    [FactIfPg]
    public async Task World_Scoped_To_Different_Subpath_Denies_Other_Subpath()
    {
        // Permission lists only /foo for the seed space; probe /bar — zero.
        await using var h = await WorldScopeHarness.CreateAsync(_factory,
            subpathsForSpace: new() { "/foo" }, // literal; walk builds "bar" for /bar probe
            actions: new() { "view", "query" },
            resourceTypes: new() { "content" },
            subpath: "/bar",
            seeds: new[] { ("x", 1) });

        var resp = await h.Client.PostAsJsonAsync("/public/query",
            new Query { Type = QueryType.Search, SpaceName = h.Space, Subpath = h.Subpath,
                        FilterSchemaNames = new(), Limit = 10 },
            DmartJsonContext.Default.Query);
        var r = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        (r!.Records?.Count ?? 0).ShouldBe(0, "subpath outside the permission's list must NOT match");
    }

    [FactIfPg]
    public async Task World_Inactive_Permission_Grants_Nothing()
    {
        // is_active=false on the permission row → walk must skip it.
        await using var h = await WorldScopeHarness.CreateAsync(_factory,
            subpathsForSpace: new() { PermissionService.AllSubpathsMw },
            actions: new() { "view", "query" },
            resourceTypes: new() { "content" },
            permIsActive: false,
            seeds: new[] { ("x", 1) });

        var resp = await h.Client.PostAsJsonAsync("/public/query",
            new Query { Type = QueryType.Search, SpaceName = h.Space, Subpath = h.Subpath,
                        FilterSchemaNames = new(), Limit = 10 },
            DmartJsonContext.Default.Query);
        var r = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        (r!.Records?.Count ?? 0).ShouldBe(0, "inactive permission must be ignored");
    }

    [FactIfPg]
    public async Task World_ResourceType_Mismatch_Denies()
    {
        // Permission grants "folder" only; we seed content → zero even though
        // the space+subpath match.
        await using var h = await WorldScopeHarness.CreateAsync(_factory,
            subpathsForSpace: new() { PermissionService.AllSubpathsMw },
            actions: new() { "view", "query" },
            resourceTypes: new() { "folder" },
            seeds: new[] { ("x", 1) });

        var resp = await h.Client.PostAsJsonAsync("/public/query",
            new Query { Type = QueryType.Search, SpaceName = h.Space, Subpath = h.Subpath,
                        FilterSchemaNames = new(), Limit = 10 },
            DmartJsonContext.Default.Query);
        var r = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        (r!.Records?.Count ?? 0).ShouldBe(0, "resource_type mismatch must deny");
    }

    [FactIfPg]
    public async Task World_View_Only_With_IsActive_Condition_Denies_Without_Query_Action()
    {
        // actions=["view"] (no "query") + conditions=["is_active"]:
        //   - "view" fails the condition check at the probe level (no resource loaded → is_active not in achieved set).
        //   - "query" fallback isn't listed → Python-parity bypass doesn't apply.
        //   Net: anonymous sees nothing.
        await using var h = await WorldScopeHarness.CreateAsync(_factory,
            subpathsForSpace: new() { PermissionService.AllSubpathsMw },
            actions: new() { "view" },
            resourceTypes: new() { "content" },
            conditions: new() { "is_active" },
            seeds: new[] { ("x", 1) });

        var resp = await h.Client.PostAsJsonAsync("/public/query",
            new Query { Type = QueryType.Search, SpaceName = h.Space, Subpath = h.Subpath,
                        FilterSchemaNames = new(), Limit = 10 },
            DmartJsonContext.Default.Query);
        var r = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        (r!.Records?.Count ?? 0).ShouldBe(0,
            "view alone + is_active condition cannot pass without a resource context; query action not listed");
    }
}

// Shared harness: create anonymous user + role + world permission + a
// throwaway space with seeded content entries, return an HTTP client with
// NO Authorization header, clean everything up on disposal. Preserves any
// pre-existing anonymous/world rows so shared DBs aren't disturbed.
internal sealed class WorldScopeHarness : IAsyncDisposable
{
    public required HttpClient Client { get; init; }
    public required string Space { get; init; }
    public required string Subpath { get; init; }

    private UserRepository _users = null!;
    private AccessRepository _access = null!;
    private EntryRepository _entries = null!;
    private string _anonUser = null!;
    private string _anonRole = null!;
    private string _worldPerm = null!;
    private User? _priorAnon;
    private Permission? _priorWorld;
    private (string shortname, int rank)[] _seeded = Array.Empty<(string, int)>();

    public static async Task<WorldScopeHarness> CreateAsync(
        DmartFactory factory,
        Dictionary<string, List<string>>? subpaths = null,
        List<string>? subpathsForSpace = null,
        List<string>? actions = null,
        List<string>? resourceTypes = null,
        List<string>? conditions = null,
        bool permIsActive = true,
        string? subpath = null,
        bool seedSpace = false,
        (string shortname, int rank)[]? seeds = null)
    {
        factory.CreateClient();
        var users = factory.Services.GetRequiredService<UserRepository>();
        var access = factory.Services.GetRequiredService<AccessRepository>();
        var entries = factory.Services.GetRequiredService<EntryRepository>();

        const string anonUser = "anonymous";
        const string worldPerm = "world";
        var anonRole = $"itest_anon_role_{Guid.NewGuid():N}"[..24];
        var space = $"itest_space_{Guid.NewGuid():N}"[..20];
        var effectiveSubpath = subpath ?? "/items";

        var priorAnon = await users.GetByShortnameAsync(anonUser);
        var priorWorld = await access.GetPermissionAsync(worldPerm);

        var effectiveSubpaths = subpaths
            ?? new Dictionary<string, List<string>> { [space] = subpathsForSpace ?? new() { effectiveSubpath } };

        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = worldPerm,
            SpaceName = "management",
            Subpath = "permissions",
            OwnerShortname = "dmart",
            IsActive = permIsActive,
            Subpaths = effectiveSubpaths,
            ResourceTypes = resourceTypes ?? new() { "content" },
            Actions = actions ?? new() { "view", "query" },
            Conditions = conditions ?? new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = anonRole,
            SpaceName = "management",
            Subpath = "roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { worldPerm },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = anonUser,
            SpaceName = "management",
            Subpath = "users",
            OwnerShortname = anonUser,
            IsActive = true,
            Roles = new() { anonRole },
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        if (seeds is { Length: > 0 })
        {
            foreach (var (shortname, rank) in seeds)
            {
                var body = JsonDocument.Parse($"{{\"rank\":{rank}}}").RootElement.Clone();
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = shortname,
                    SpaceName = space,
                    Subpath = effectiveSubpath,
                    OwnerShortname = "dmart",
                    ResourceType = ResourceType.Content,
                    IsActive = true,
                    Payload = new Payload { ContentType = ContentType.Json, Body = body },
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
        }
        await access.InvalidateAllCachesAsync();

        return new WorldScopeHarness
        {
            Client = factory.CreateClient(),
            Space = space,
            Subpath = effectiveSubpath,
            _users = users,
            _access = access,
            _entries = entries,
            _anonUser = anonUser,
            _anonRole = anonRole,
            _worldPerm = worldPerm,
            _priorAnon = priorAnon,
            _priorWorld = priorWorld,
            _seeded = seeds ?? Array.Empty<(string, int)>(),
        };
        // Note: `_` prefix fields are initialized via the object-initializer
        // syntax above. If C# blocks non-public field init, refactor to a
        // constructor — but the `required` props and record-ish init keep it
        // readable.
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (shortname, _) in _seeded)
        {
            try { await _entries.DeleteAsync(Space, Subpath, shortname, ResourceType.Content); } catch { }
        }
        try { await _users.DeleteAsync(_anonUser); } catch { }
        try { await _access.DeleteRoleAsync(_anonRole); } catch { }
        try { await _access.DeletePermissionAsync(_worldPerm); } catch { }
        if (_priorAnon is not null)  await _users.UpsertAsync(_priorAnon);
        if (_priorWorld is not null) await _access.UpsertPermissionAsync(_priorWorld);
        await _access.InvalidateAllCachesAsync();
        Client.Dispose();
    }
}
