using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Integration tests for the /managed/request dispatch that commit 8cbb999
// added for Role and Permission resource types (previously fell through to
// EntryService, a silent no-op). Also pins the aggregate-failure wire shape
// — Python's info=[{successfull,failed}] with the "successfull" typo
// preserved — that the same commit introduced.
public sealed class RolePermissionRequestTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public RolePermissionRequestTests(DmartFactory factory) => _factory = factory;

    // ==================== Role update/delete via /managed/request ====================

    [Fact]
    public async Task Role_Update_Via_Request_Persists_Attributes()
    {
        if (!DmartFactory.HasPg) return;
        var client = await AuthedClient();
        var access = _factory.Services.GetRequiredService<AccessRepository>();

        var role = $"rupd_{Guid.NewGuid():N}"[..20];
        var perm = $"rupd_p_{Guid.NewGuid():N}"[..20];

        try
        {
            // Seed a role with one permission.
            await CreateRecord(client, ResourceType.Permission, "/permissions", perm, new()
            {
                ["is_active"] = true,
                ["subpaths"] = new Dictionary<string, object> { ["test"] = new List<string> { "users" } },
                ["resource_types"] = new List<string> { "content" },
                ["actions"] = new List<string> { "view" },
                ["conditions"] = new List<string>(),
            });
            await CreateRecord(client, ResourceType.Role, "/roles", role, new()
            {
                ["is_active"] = true,
                ["permissions"] = new List<string> { perm },
            });

            // Update: add "tags", flip is_active=false, change displayname.
            var updateResp = await PostRequest(client, RequestType.Update, "management", new Record
            {
                ResourceType = ResourceType.Role,
                Subpath = "/roles",
                Shortname = role,
                Attributes = new()
                {
                    ["tags"] = new List<string> { "audited" },
                    ["is_active"] = false,
                    ["displayname"] = new Dictionary<string, object> { ["en"] = "Renamed Role" },
                },
            });
            updateResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Confirm DB persistence.
            var saved = await access.GetRoleAsync(role);
            saved.ShouldNotBeNull();
            saved!.IsActive.ShouldBeFalse();
            saved.Tags.ShouldContain("audited");
            saved.Displayname!.En.ShouldBe("Renamed Role");
            // Permissions list was NOT in the patch → must remain untouched.
            saved.Permissions.ShouldContain(perm);
        }
        finally
        {
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
        }
    }

    [Fact]
    public async Task Role_Delete_Via_Request_Removes_Row()
    {
        if (!DmartFactory.HasPg) return;
        var client = await AuthedClient();
        var access = _factory.Services.GetRequiredService<AccessRepository>();

        var role = $"rdel_{Guid.NewGuid():N}"[..20];

        await CreateRecord(client, ResourceType.Role, "/roles", role, new()
        {
            ["is_active"] = true,
            ["permissions"] = new List<string>(),
        });
        (await access.GetRoleAsync(role)).ShouldNotBeNull();

        var deleteResp = await PostRequest(client, RequestType.Delete, "management", new Record
        {
            ResourceType = ResourceType.Role,
            Subpath = "/roles",
            Shortname = role,
        });
        deleteResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await access.GetRoleAsync(role)).ShouldBeNull("DELETE must remove the roles row");
    }

    [Fact]
    public async Task Role_Delete_Unknown_Returns_OBJECT_NOT_FOUND()
    {
        if (!DmartFactory.HasPg) return;
        var client = await AuthedClient();

        var deleteResp = await PostRequest(client, RequestType.Delete, "management", new Record
        {
            ResourceType = ResourceType.Role,
            Subpath = "/roles",
            Shortname = "definitely_not_a_real_role_zzz",
        });
        // Single-record failure returns aggregate envelope (commit 8cbb999).
        var body = await deleteResp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.SOMETHING_WRONG);
        body.Error.Info.ShouldNotBeNull();
        var failed = (System.Text.Json.JsonElement)body.Error.Info![0]["failed"];
        failed.GetArrayLength().ShouldBe(1);
        failed[0].GetProperty("error_code").GetInt32().ShouldBe(InternalErrorCode.OBJECT_NOT_FOUND);
    }

    // ==================== Permission update/delete via /managed/request ====================

    [Fact]
    public async Task Permission_Update_Via_Request_Persists_Attributes()
    {
        if (!DmartFactory.HasPg) return;
        var client = await AuthedClient();
        var access = _factory.Services.GetRequiredService<AccessRepository>();

        var perm = $"pupd_{Guid.NewGuid():N}"[..20];

        try
        {
            await CreateRecord(client, ResourceType.Permission, "/permissions", perm, new()
            {
                ["is_active"] = true,
                ["subpaths"] = new Dictionary<string, object> { ["test"] = new List<string> { "a" } },
                ["resource_types"] = new List<string> { "content" },
                ["actions"] = new List<string> { "view" },
                ["conditions"] = new List<string>(),
            });

            var updateResp = await PostRequest(client, RequestType.Update, "management", new Record
            {
                ResourceType = ResourceType.Permission,
                Subpath = "/permissions",
                Shortname = perm,
                Attributes = new()
                {
                    ["actions"] = new List<string> { "view", "query" },
                    ["resource_types"] = new List<string> { "content", "folder" },
                    ["subpaths"] = new Dictionary<string, object> { ["test"] = new List<string> { "b" } },
                },
            });
            updateResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var saved = await access.GetPermissionAsync(perm);
            saved.ShouldNotBeNull();
            saved!.Actions.ShouldContain("query");
            saved.ResourceTypes.ShouldContain("folder");
            saved.Subpaths["test"].ShouldContain("b");
        }
        finally
        {
            try { await access.DeletePermissionAsync(perm); } catch { }
        }
    }

    [Fact]
    public async Task Permission_Delete_Via_Request_Removes_Row()
    {
        if (!DmartFactory.HasPg) return;
        var client = await AuthedClient();
        var access = _factory.Services.GetRequiredService<AccessRepository>();

        var perm = $"pdel_{Guid.NewGuid():N}"[..20];
        await CreateRecord(client, ResourceType.Permission, "/permissions", perm, new()
        {
            ["is_active"] = true,
            ["subpaths"] = new Dictionary<string, object> { ["test"] = new List<string> { "x" } },
            ["resource_types"] = new List<string> { "content" },
            ["actions"] = new List<string> { "view" },
        });
        (await access.GetPermissionAsync(perm)).ShouldNotBeNull();

        var deleteResp = await PostRequest(client, RequestType.Delete, "management", new Record
        {
            ResourceType = ResourceType.Permission,
            Subpath = "/permissions",
            Shortname = perm,
        });
        deleteResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await access.GetPermissionAsync(perm)).ShouldBeNull();
    }

    // ==================== Aggregate failure shape ====================

    [Fact]
    public async Task Multi_Record_Delete_Returns_Aggregate_Successfull_And_Failed()
    {
        if (!DmartFactory.HasPg) return;
        var client = await AuthedClient();
        var access = _factory.Services.GetRequiredService<AccessRepository>();

        var goodRole = $"agg_ok_{Guid.NewGuid():N}"[..20];

        await CreateRecord(client, ResourceType.Role, "/roles", goodRole, new()
        {
            ["is_active"] = true,
            ["permissions"] = new List<string>(),
        });

        try
        {
            // Batch: one that will succeed, one that will fail (unknown role).
            var req = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = "management",
                Records = new()
                {
                    new() { ResourceType = ResourceType.Role, Subpath = "/roles", Shortname = goodRole },
                    new() { ResourceType = ResourceType.Role, Subpath = "/roles", Shortname = "ghost_role_zz" },
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
            var raw = await resp.Content.ReadAsStringAsync();

            // Python parity: aggregate failure is HTTP 400, code SOMETHING_WRONG.
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.SOMETHING_WRONG);
            body.Error.Type.ShouldBe("request");
            body.Error.Info.ShouldNotBeNull();
            body.Error.Info!.Count.ShouldBe(1);

            var first = body.Error.Info[0];
            // The Python "successfull" typo MUST be on the wire — clients branch on it.
            first.ShouldContainKey("successfull");
            first.ShouldContainKey("failed");
            // Raw string check too so regressions on the key name can't sneak past.
            raw.ShouldContain("\"successfull\"");

            var successfull = (JsonElement)first["successfull"];
            successfull.GetArrayLength().ShouldBe(1);

            var failed = (JsonElement)first["failed"];
            failed.GetArrayLength().ShouldBe(1);
            failed[0].GetProperty("record").GetString().ShouldBe("ghost_role_zz");
            failed[0].GetProperty("error_code").GetInt32().ShouldBe(InternalErrorCode.OBJECT_NOT_FOUND);
        }
        finally
        {
            try { await access.DeleteRoleAsync(goodRole); } catch { }
        }
    }

    [Fact]
    public async Task All_Records_Succeed_Returns_Ok_Without_Error()
    {
        // Regression guard: aggregate shape must NOT kick in when every record succeeds.
        if (!DmartFactory.HasPg) return;
        var client = await AuthedClient();
        var access = _factory.Services.GetRequiredService<AccessRepository>();

        var r1 = $"agg_r1_{Guid.NewGuid():N}"[..20];
        var r2 = $"agg_r2_{Guid.NewGuid():N}"[..20];

        try
        {
            var req = new Request
            {
                RequestType = RequestType.Create,
                SpaceName = "management",
                Records = new()
                {
                    MakeRoleRecord(r1),
                    MakeRoleRecord(r2),
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);
            body.Error.ShouldBeNull();
            body.Records!.Count.ShouldBe(2);
        }
        finally
        {
            try { await access.DeleteRoleAsync(r1); } catch { }
            try { await access.DeleteRoleAsync(r2); } catch { }
        }
    }

    // ==================== helpers ====================

    private async Task<HttpClient> AuthedClient()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"login failed: {resp.StatusCode} {raw}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task CreateRecord(HttpClient client, ResourceType rt, string subpath, string shortname, Dictionary<string, object> attrs)
    {
        var req = new Request
        {
            RequestType = RequestType.Create,
            SpaceName = "management",
            Records = new() { new() { ResourceType = rt, Subpath = subpath, Shortname = shortname, Attributes = attrs } },
        };
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var raw = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"CreateRecord({rt}/{shortname}) failed: {resp.StatusCode}\n{raw}");
        }
    }

    private static Task<HttpResponseMessage> PostRequest(HttpClient client, RequestType rt, string space, Record record)
    {
        var req = new Request
        {
            RequestType = rt,
            SpaceName = space,
            Records = new() { record },
        };
        return client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
    }

    private static Record MakeRoleRecord(string shortname) => new()
    {
        ResourceType = ResourceType.Role,
        Subpath = "/roles",
        Shortname = shortname,
        Attributes = new()
        {
            ["is_active"] = true,
            ["permissions"] = new List<string>(),
        },
    };
}
