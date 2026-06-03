using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Auth;
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

public sealed class AuthzRegressionTests : IClassFixture<DmartFactory>
{
    private const string Password = "Test1234";
    private readonly DmartFactory _factory;

    public AuthzRegressionTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Managed_Direct_Reads_Deny_Unpermitted_Limited_User()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var viewer = Unique("authz_user");
        var targetUser = Unique("authz_tuser");
        var targetRole = Unique("authz_role");
        var targetPerm = Unique("authz_perm");
        var targetSpace = Unique("authz_space");
        var now = DateTime.UtcNow;

        await CreateUserAsync(users, hasher, viewer);
        await CreateUserAsync(users, hasher, targetUser);
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = targetRole,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = targetPerm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [targetSpace] = new() { PermissionService.AllSubpathsMw } },
            ResourceTypes = new() { "content" },
            Actions = new() { "view" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = targetSpace,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.InvalidateAllCachesAsync();

        try
        {
            var (client, _) = await LoginAsAsync(viewer);

            (await client.GetAsync($"/managed/entry/user/management/users/{targetUser}"))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync($"/managed/entry/role/management/roles/{targetRole}"))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync($"/managed/entry/permission/management/permissions/{targetPerm}"))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync($"/managed/entry/space/management/{targetSpace}"))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(viewer); } catch { }
            try { await users.DeleteAsync(viewer); } catch { }
            try { await users.DeleteAsync(targetUser); } catch { }
            try { await access.DeleteRoleAsync(targetRole); } catch { }
            try { await access.DeletePermissionAsync(targetPerm); } catch { }
            try { await spaces.DeleteAsync(targetSpace); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task Tags_And_Aggregation_Apply_Row_Level_Acl()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var user = Unique("acl_user");
        var role = Unique("acl_role");
        var perm = Unique("acl_perm");
        var space = Unique("acl_space");
        var subpath = $"/acl_{Guid.NewGuid():N}"[..17];
        var visible = Unique("acl_visible");
        var hidden = Unique("acl_hidden");
        var visibleTag = Unique("visible_tag");
        var hiddenTag = Unique("hidden_tag");
        var now = DateTime.UtcNow;

        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [space] = new() { subpath } },
            ResourceTypes = new() { "content" },
            Actions = new() { "view", "query" },
            Conditions = new() { "is_active" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { perm },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, user, new() { role });
        await entries.UpsertAsync(BuildContent(space, subpath, visible, isActive: true, visibleTag, "visible", now));
        await entries.UpsertAsync(BuildContent(space, subpath, hidden, isActive: false, hiddenTag, "hidden", now));
        await access.InvalidateAllCachesAsync();

        try
        {
            var (client, _) = await LoginAsAsync(user);

            var tagsResp = await client.PostAsJsonAsync("/managed/query", new Query
            {
                Type = QueryType.Tags,
                SpaceName = space,
                Subpath = subpath,
                FilterSchemaNames = new(),
                Limit = 20,
            }, DmartJsonContext.Default.Query);
            var tagsRaw = await tagsResp.Content.ReadAsStringAsync();
            tagsResp.StatusCode.ShouldBe(HttpStatusCode.OK, tagsRaw);
            using (var tagsDoc = JsonDocument.Parse(tagsRaw))
            {
                tagsDoc.RootElement.GetProperty("status").GetString().ShouldBe("success");
                var tags = tagsDoc.RootElement.GetProperty("records")[0]
                    .GetProperty("attributes")
                    .GetProperty("tags")
                    .EnumerateArray()
                    .Select(t => t.GetString())
                    .ToArray();
                tags.ShouldContain(visibleTag);
                tags.ShouldNotContain(hiddenTag);
            }

            var aggregationResp = await client.PostAsJsonAsync("/managed/query", new Query
            {
                Type = QueryType.Aggregation,
                SpaceName = space,
                Subpath = subpath,
                FilterSchemaNames = new(),
                SortBy = "@payload.body.bucket",
                SortType = SortType.Ascending,
                Limit = 20,
                AggregationData = new RedisAggregate
                {
                    GroupBy = new() { "@payload.body.bucket" },
                    // Omit args on the wire; Redis COUNT reducers commonly rely on the default.
                    Reducers = new() { new RedisReducer { ReducerName = "count", Alias = "count", Args = null! } },
                },
            }, DmartJsonContext.Default.Query);
            var aggregationRaw = await aggregationResp.Content.ReadAsStringAsync();
            aggregationResp.StatusCode.ShouldBe(HttpStatusCode.OK, aggregationRaw);
            using (var aggregationDoc = JsonDocument.Parse(aggregationRaw))
            {
                aggregationDoc.RootElement.GetProperty("status").GetString().ShouldBe("success");
                var records = aggregationDoc.RootElement.GetProperty("records").EnumerateArray().ToArray();
                var buckets = records
                    .Select(r => r.GetProperty("attributes").GetProperty("payload_body_bucket").GetString())
                    .ToArray();
                buckets.ShouldContain("visible");
                buckets.ShouldNotContain("hidden");
                var visibleRecord = records.Single(r =>
                    r.GetProperty("attributes").GetProperty("payload_body_bucket").GetString() == "visible");
                visibleRecord.GetProperty("attributes").GetProperty("count").GetInt32().ShouldBe(1);
            }
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, visible, ResourceType.Content); } catch { }
            try { await entries.DeleteAsync(space, subpath, hidden, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(user); } catch { }
            try { await users.DeleteAsync(user); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // Regression: a permission scoped to non-Content resource_types (e.g.
    // ["ticket"]) used to return an empty result for entries-table queries
    // because the preflight `CanQueryAsync(actor, ResourceType.Content, ...)`
    // probed for a Content-listed permission specifically. Python parity
    // (adapter.py:1510-1520, query_policies_helper.py) is resource-type-
    // agnostic — only the policy-empty check gates. With the preflight
    // dropped, a user whose only permission lists ["ticket"] now sees their
    // tickets.
    [FactIfPg]
    public async Task QueryEntries_Honors_NonContent_ResourceType_Permission()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var user = Unique("ticketonly_user");
        var role = Unique("ticketonly_role");
        var perm = Unique("ticketonly_perm");
        var space = Unique("ticketonly_space");
        var subpath = $"/sp_{Guid.NewGuid():N}"[..14];
        var ticketSn = Unique("tk");
        var now = DateTime.UtcNow;

        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });
        // Permission lists `ticket` ONLY. No `content`. Pre-fix this user's
        // /managed/query returns total=0 even when filter_types=["ticket"].
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [space] = new() { subpath } },
            ResourceTypes = new() { "ticket" },
            Actions = new() { "view", "query" },
            Conditions = new() { "is_active" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { perm },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, user, new() { role });
        // Seed a ticket the user should be able to see.
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ticketSn,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = user,
            ResourceType = ResourceType.Ticket,
            IsActive = true,
            State = "new",
            IsOpen = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.InvalidateAllCachesAsync();

        try
        {
            var (client, _) = await LoginAsAsync(user);

            var resp = await client.PostAsJsonAsync("/managed/query", new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = subpath,
                FilterTypes = new() { ResourceType.Ticket },
                FilterSchemaNames = new(),
                Limit = 10,
            }, DmartJsonContext.Default.Query);
            var raw = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
            using var doc = JsonDocument.Parse(raw);
            doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
            // Pre-fix this would be 0 because the Content-specific preflight bailed.
            doc.RootElement.GetProperty("attributes").GetProperty("total").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
            var shortnames = doc.RootElement.GetProperty("records").EnumerateArray()
                .Select(r => r.GetProperty("shortname").GetString())
                .ToArray();
            shortnames.ShouldContain(ticketSn);
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, ticketSn, ResourceType.Ticket); } catch { }
            try { await users.DeleteAllSessionsAsync(user); } catch { }
            try { await users.DeleteAsync(user); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // End-to-end pin for the filter_fields_values merge: a permission whose
    // FFV is "@state:active" must restrict the SQL result set to entries
    // whose State column equals "active". Pre-merge wiring this would return
    // both rows because the permission grants access to the whole subpath.
    [FactIfPg]
    public async Task FilterFieldsValues_NarrowsResultSet_ByState()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var user = Unique("ffv_user");
        var role = Unique("ffv_role");
        var perm = Unique("ffv_perm");
        var space = Unique("ffv_space");
        var subpath = $"/sp_{Guid.NewGuid():N}"[..14];
        var activeSn = Unique("ffv_active");
        var archivedSn = Unique("ffv_archived");
        var now = DateTime.UtcNow;

        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [space] = new() { subpath } },
            ResourceTypes = new() { "content" },
            Actions = new() { "view", "query" },
            // Row-level ACL: only entries with State="active" are visible.
            FilterFieldsValues = "@state:active",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { perm },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, user, new() { role });

        // Two entries, same subpath, same resource type; only `state` differs.
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = activeSn,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = "dmart",
            ResourceType = ResourceType.Content,
            IsActive = true,
            State = "active",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = archivedSn,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = "dmart",
            ResourceType = ResourceType.Content,
            IsActive = true,
            State = "archived",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.InvalidateAllCachesAsync();

        try
        {
            var (client, _) = await LoginAsAsync(user);

            var resp = await client.PostAsJsonAsync("/managed/query", new Query
            {
                Type = QueryType.Subpath,
                SpaceName = space,
                Subpath = subpath,
                FilterSchemaNames = new(),
                Limit = 50,
            }, DmartJsonContext.Default.Query);
            var raw = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, raw);

            using var doc = JsonDocument.Parse(raw);
            doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
            var shortnames = doc.RootElement.GetProperty("records").EnumerateArray()
                .Select(r => r.GetProperty("shortname").GetString())
                .ToArray();

            // The FFV restriction is the load-bearing assertion: only the
            // active row may surface; the archived row must be filtered out
            // even though the permission's subpath grant covers both.
            shortnames.ShouldContain(activeSn);
            shortnames.ShouldNotContain(archivedSn);
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, activeSn, ResourceType.Content); } catch { }
            try { await entries.DeleteAsync(space, subpath, archivedSn, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(user); } catch { }
            try { await users.DeleteAsync(user); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    private static async Task CreateUserAsync(
        UserRepository users,
        PasswordHasher hasher,
        string shortname,
        List<string>? roles = null)
    {
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(Password),
            Type = UserType.Web,
            Language = Language.En,
            Roles = roles ?? new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task<(HttpClient Client, string Token)> LoginAsAsync(string shortname)
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(shortname, null, null, Password, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, raw);

        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"Login failed for '{shortname}': {raw}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    [FactIfPg]
    public async Task GrantableBy_Governs_Role_Assignment_On_Managed_User_Create()
    {
        _factory.CreateClient();
        var users  = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var mgr      = Unique("gb_mgr");
        var mgrRole  = Unique("gb_mgrrole");
        var mgrPerm  = Unique("gb_mgrperm");
        var editor   = Unique("gb_editor");
        var secret   = Unique("gb_secret");
        var newUserOk   = Unique("gb_newok");
        var newUserDeny = Unique("gb_newdeny");
        var now = DateTime.UtcNow;

        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = mgrPerm,
            SpaceName = "management", Subpath = "/permissions", OwnerShortname = "dmart", IsActive = true,
            Subpaths = new() { ["management"] = new() { "users" } },
            ResourceTypes = new() { "user" },
            Actions = new() { "create", "update" },
            CreatedAt = now, UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = mgrRole,
            SpaceName = "management", Subpath = "/roles", OwnerShortname = "dmart", IsActive = true,
            Permissions = new() { mgrPerm }, CreatedAt = now, UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = editor,
            SpaceName = "management", Subpath = "/roles", OwnerShortname = "dmart", IsActive = true,
            GrantableBy = new() { mgrRole }, CreatedAt = now, UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = secret,
            SpaceName = "management", Subpath = "/roles", OwnerShortname = "dmart", IsActive = true,
            CreatedAt = now, UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, mgr, new List<string> { mgrRole });
        await access.InvalidateAllCachesAsync();

        try
        {
            var (client, _) = await LoginAsAsync(mgr);

            // ALLOW: editor is grantable to mgrRole holders.
            var ok = await PostManaged(client, RequestType.Create, ResourceType.User, "/users", newUserOk,
                new() { ["roles"] = new List<string> { editor }, ["is_active"] = true });
            ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

            // DENY: secret is not delegated to the manager → aggregate failure → HTTP 400.
            var deny = await PostManaged(client, RequestType.Create, ResourceType.User, "/users", newUserDeny,
                new() { ["roles"] = new List<string> { secret }, ["is_active"] = true });
            deny.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await deny.Content.ReadAsStringAsync());

            // GATE: a non-global-admin may not set grantable_by on a role.
            var gate = await PostManaged(client, RequestType.Update, ResourceType.Role, "/roles", editor,
                new() { ["grantable_by"] = new List<string> { mgrRole, secret } });
            gate.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await gate.Content.ReadAsStringAsync());
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(mgr); } catch { }
            try { await users.DeleteAsync(mgr); } catch { }
            try { await users.DeleteAsync(newUserOk); } catch { }
            try { await users.DeleteAsync(newUserDeny); } catch { }
            try { await access.DeleteRoleAsync(mgrRole); } catch { }
            try { await access.DeleteRoleAsync(editor); } catch { }
            try { await access.DeleteRoleAsync(secret); } catch { }
            try { await access.DeletePermissionAsync(mgrPerm); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    private static Task<HttpResponseMessage> PostManaged(
        HttpClient client, RequestType rt, ResourceType resource, string subpath,
        string shortname, Dictionary<string, object> attrs)
    {
        var req = new Request
        {
            RequestType = rt,
            SpaceName = "management",
            Records = new() { new() { ResourceType = resource, Subpath = subpath, Shortname = shortname, Attributes = attrs } },
        };
        return client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
    }

    private static Entry BuildContent(
        string space,
        string subpath,
        string shortname,
        bool isActive,
        string tag,
        string bucket,
        DateTime now)
    {
        var body = JsonDocument.Parse($$"""{"bucket":"{{bucket}}"}""").RootElement.Clone();
        return new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = "dmart",
            ResourceType = ResourceType.Content,
            IsActive = isActive,
            Tags = new() { tag },
            Payload = new Payload { ContentType = ContentType.Json, Body = body },
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
