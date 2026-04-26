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
