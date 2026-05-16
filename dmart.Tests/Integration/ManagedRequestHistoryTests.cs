using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the audit history side-effects of /managed/request for non-entry
// resource types (User, Role, Permission, Space). The dispatcher previously
// called the dedicated repository's UpsertAsync directly with no history
// append — clients reading /managed/query?type=history saw no audit trail
// for any admin-driven CRUD on these types, even though entry updates and
// self-service profile edits already wrote diffs.
//
// Contract (Python parity): only UPDATE writes a history row. Create and
// Delete must not produce history; create-side fields aren't field-changes
// (no `old` to point at) and delete-side rows would survive a row that's
// already gone, which clutters /managed/query?type=history with rows whose
// resource is missing.
public sealed class ManagedRequestHistoryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedRequestHistoryTests(DmartFactory factory) => _factory = factory;

    // ==================== User ====================

    [FactIfPg]
    public async Task User_Update_Writes_OldNew_Diff_History_Row()
    {
        var caller = await AuthedCaller();
        var client = caller.Client;
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var qsvc = _factory.Services.GetRequiredService<QueryService>();

        var sn = $"uhist_{Guid.NewGuid():N}"[..14];
        try
        {
            // Create — must not write history.
            await CreateRecord(client, ResourceType.User, "/users", sn, new()
            {
                ["email"] = $"{sn}@test.local",
                ["is_active"] = true,
                ["language"] = "en",
                ["is_email_verified"] = true,
            });
            (await QueryHistory(qsvc, "management", "/users", sn))
                .Records!.Count.ShouldBe(0);

            // Update: change email + flip language.
            var updateResp = await PostRequest(client, RequestType.Update, "management", new Record
            {
                ResourceType = ResourceType.User,
                Subpath = "/users",
                Shortname = sn,
                Attributes = new()
                {
                    ["email"] = $"new_{sn}@test.local",
                    ["language"] = "ar",
                },
            });
            updateResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // After update: exactly one history row carrying the update diff.
            var afterUpdate = await QueryHistory(qsvc, "management", "/users", sn);
            afterUpdate.Records!.Count.ShouldBe(1);
            var updateDiff = (JsonElement)afterUpdate.Records[0].Attributes!["diff"]!;
            updateDiff.TryGetProperty("email", out var emailUpd).ShouldBeTrue();
            emailUpd.GetProperty("old").GetString().ShouldBe($"{sn}@test.local");
            emailUpd.GetProperty("new").GetString().ShouldBe($"new_{sn}@test.local");
            updateDiff.TryGetProperty("language", out var langUpd).ShouldBeTrue();
            langUpd.GetProperty("old").GetString().ShouldBe("english");
            langUpd.GetProperty("new").GetString().ShouldBe("arabic");
            // Audit attribution: the admin caller, not the target user — an
            // admin path that recorded the target's shortname would leak no
            // information about who actually performed the write.
            afterUpdate.Records[0].Attributes!["owner_shortname"]!.ToString()
                .ShouldBe(caller.Shortname);
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task User_Patch_Writes_History_Row()
    {
        // The dispatcher matches `RequestType.Update or RequestType.Patch` in
        // a single arm (RequestHandler.DispatchUpdateAsync), so Update is the
        // de facto Patch test too — but a regression that broke only one of
        // the two arms (e.g. an enum reshuffle or a new arm split) would slip
        // past tests that only exercise Update. This pins Patch independently.
        var caller = await AuthedCaller();
        var client = caller.Client;
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var qsvc = _factory.Services.GetRequiredService<QueryService>();

        var sn = $"upatch_{Guid.NewGuid():N}"[..14];
        try
        {
            await CreateRecord(client, ResourceType.User, "/users", sn, new()
            {
                ["email"] = $"{sn}@test.local",
                ["is_active"] = true,
                ["language"] = "en",
                ["is_email_verified"] = true,
            });

            var patchResp = await PostRequest(client, RequestType.Patch, "management", new Record
            {
                ResourceType = ResourceType.User,
                Subpath = "/users",
                Shortname = sn,
                Attributes = new() { ["email"] = $"patched_{sn}@test.local" },
            });
            patchResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var hist = await QueryHistory(qsvc, "management", "/users", sn);
            hist.Records!.Count.ShouldBe(1);
            var diff = (JsonElement)hist.Records[0].Attributes!["diff"]!;
            diff.TryGetProperty("email", out var emailDiff).ShouldBeTrue();
            emailDiff.GetProperty("old").GetString().ShouldBe($"{sn}@test.local");
            emailDiff.GetProperty("new").GetString().ShouldBe($"patched_{sn}@test.local");
            hist.Records[0].Attributes!["owner_shortname"]!.ToString()
                .ShouldBe(caller.Shortname);
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task User_NoOp_Update_Skips_History_Append()
    {
        // Regression: an update that resolves to zero diff (re-posting the
        // current state) must not pollute the audit log with empty rows.
        // Create writes no history either, so the table stays empty.
        var caller = await AuthedCaller();
        var client = caller.Client;
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var qsvc = _factory.Services.GetRequiredService<QueryService>();

        var sn = $"unoop_{Guid.NewGuid():N}"[..14];
        try
        {
            await CreateRecord(client, ResourceType.User, "/users", sn, new()
            {
                ["email"] = $"{sn}@test.local",
                ["is_active"] = true,
                ["language"] = "en",
                ["is_email_verified"] = true,
            });

            // No-op update — same email/language as create.
            var resp = await PostRequest(client, RequestType.Update, "management", new Record
            {
                ResourceType = ResourceType.User, Subpath = "/users", Shortname = sn,
                Attributes = new()
                {
                    ["email"] = $"{sn}@test.local",
                    ["language"] = "en",
                },
            });
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await QueryHistory(qsvc, "management", "/users", sn))
                .Records!.Count.ShouldBe(0);
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task User_Delete_Does_Not_Append_History_Row()
    {
        // Delete itself MUST NOT add a row to histories: deletes don't have
        // a meaningful {old, new} (the row is going away), and a delete-side
        // audit row pointing at a resource that no longer exists clutters
        // /managed/query?type=history with dangling entries.
        var caller = await AuthedCaller();
        var client = caller.Client;
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var qsvc = _factory.Services.GetRequiredService<QueryService>();

        var sn = $"udelap_{Guid.NewGuid():N}"[..14];
        try
        {
            await CreateRecord(client, ResourceType.User, "/users", sn, new()
            {
                ["email"] = $"{sn}@test.local",
                ["is_active"] = true,
                ["language"] = "en",
                ["is_email_verified"] = true,
            });
            // Pre-condition: history table is empty (creates don't audit).
            (await QueryHistory(qsvc, "management", "/users", sn))
                .Records!.Count.ShouldBe(0);

            var deleteReq = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = "management",
                Records = new() { new() { ResourceType = ResourceType.User, Subpath = "/users", Shortname = sn } },
            };
            var deleteResp = await client.PostAsJsonAsync("/managed/request", deleteReq, DmartJsonContext.Default.Request);
            deleteResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Still empty: delete contributed nothing.
            (await QueryHistory(qsvc, "management", "/users", sn))
                .Records!.Count.ShouldBe(0);
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task User_Delete_Preserves_Prior_History_Rows()
    {
        // The histories row's lifetime is decoupled from the user row's.
        // No FK cascade from users → histories: a deleted user's audit
        // trail survives so post-mortem queries on /managed/query?type=history
        // for shortname=X still surface the row's history. Seed-update
        // makes the claim non-vacuous — without it, asserting Count after
        // delete proves nothing.
        var caller = await AuthedCaller();
        var client = caller.Client;
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var qsvc = _factory.Services.GetRequiredService<QueryService>();

        var sn = $"udelpr_{Guid.NewGuid():N}"[..14];
        try
        {
            await CreateRecord(client, ResourceType.User, "/users", sn, new()
            {
                ["email"] = $"{sn}@test.local",
                ["is_active"] = true,
                ["language"] = "en",
                ["is_email_verified"] = true,
            });

            // Seed: one update emits one history row.
            var updateResp = await PostRequest(client, RequestType.Update, "management", new Record
            {
                ResourceType = ResourceType.User,
                Subpath = "/users",
                Shortname = sn,
                Attributes = new() { ["email"] = $"upd_{sn}@test.local" },
            });
            updateResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await QueryHistory(qsvc, "management", "/users", sn))
                .Records!.Count.ShouldBe(1);

            var deleteReq = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = "management",
                Records = new() { new() { ResourceType = ResourceType.User, Subpath = "/users", Shortname = sn } },
            };
            var deleteResp = await client.PostAsJsonAsync("/managed/request", deleteReq, DmartJsonContext.Default.Request);
            deleteResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Pre-existing row survives.
            (await QueryHistory(qsvc, "management", "/users", sn))
                .Records!.Count.ShouldBe(1);
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    // ==================== Role ====================

    [FactIfPg]
    public async Task Role_Update_Writes_History_Under_MgmtRoles()
    {
        var caller = await AuthedCaller();
        var client = caller.Client;
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var qsvc = _factory.Services.GetRequiredService<QueryService>();

        var perm = $"rhp_{Guid.NewGuid():N}"[..16];
        var role = $"rh_{Guid.NewGuid():N}"[..16];
        try
        {
            await CreateRecord(client, ResourceType.Permission, "/permissions", perm, new()
            {
                ["is_active"] = true,
                ["resource_types"] = new List<string> { "content" },
                ["actions"] = new List<string> { "view" },
            });
            await CreateRecord(client, ResourceType.Role, "/roles", role, new()
            {
                ["is_active"] = true,
                ["permissions"] = new List<string> { perm },
                ["tags"] = new List<string> { "initial" },
            });

            // Creates wrote nothing.
            (await QueryHistory(qsvc, "management", "/roles", role))
                .Records!.Count.ShouldBe(0);

            var updateResp = await PostRequest(client, RequestType.Update, "management", new Record
            {
                ResourceType = ResourceType.Role,
                Subpath = "/roles",
                Shortname = role,
                Attributes = new()
                {
                    ["is_active"] = false,
                    ["tags"] = new List<string> { "audited" },
                },
            });
            updateResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var afterUpdate = await QueryHistory(qsvc, "management", "/roles", role);
            afterUpdate.Records!.Count.ShouldBe(1);
            var updateDiff = (JsonElement)afterUpdate.Records[0].Attributes!["diff"]!;
            updateDiff.TryGetProperty("is_active", out var iaDiff).ShouldBeTrue();
            iaDiff.GetProperty("old").GetBoolean().ShouldBeTrue();
            iaDiff.GetProperty("new").GetBoolean().ShouldBeFalse();
            updateDiff.TryGetProperty("tags", out _).ShouldBeTrue();
            afterUpdate.Records[0].Attributes!["owner_shortname"]!.ToString()
                .ShouldBe(caller.Shortname);
        }
        finally
        {
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
        }
    }

    // ==================== Permission ====================

    [FactIfPg]
    public async Task Permission_Update_Writes_History_With_FieldLevel_Diff()
    {
        var caller = await AuthedCaller();
        var client = caller.Client;
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var qsvc = _factory.Services.GetRequiredService<QueryService>();

        var perm = $"phist_{Guid.NewGuid():N}"[..16];
        try
        {
            await CreateRecord(client, ResourceType.Permission, "/permissions", perm, new()
            {
                ["is_active"] = true,
                ["resource_types"] = new List<string> { "content" },
                ["actions"] = new List<string> { "view" },
            });

            (await QueryHistory(qsvc, "management", "/permissions", perm))
                .Records!.Count.ShouldBe(0);

            var updateResp = await PostRequest(client, RequestType.Update, "management", new Record
            {
                ResourceType = ResourceType.Permission,
                Subpath = "/permissions",
                Shortname = perm,
                Attributes = new()
                {
                    ["actions"] = new List<string> { "view", "create" },
                },
            });
            updateResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var afterUpdate = await QueryHistory(qsvc, "management", "/permissions", perm);
            afterUpdate.Records!.Count.ShouldBe(1);
            var diff = (JsonElement)afterUpdate.Records[0].Attributes!["diff"]!;
            diff.TryGetProperty("actions", out var actDiff).ShouldBeTrue();
            actDiff.GetProperty("new").GetArrayLength().ShouldBe(2);
            afterUpdate.Records[0].Attributes!["owner_shortname"]!.ToString()
                .ShouldBe(caller.Shortname);
        }
        finally
        {
            try { await access.DeletePermissionAsync(perm); } catch { }
        }
    }

    // ==================== Space ====================

    [FactIfPg]
    public async Task Space_Update_Writes_History_With_FieldLevel_Diff()
    {
        // Space create is gated on the test factory — we seed via the
        // SpaceRepository directly and only exercise the /managed/request
        // UPDATE path which is the original bug surface for spaces. The
        // seed-then-update flow still confirms the dispatch path writes
        // history that's queryable via /managed/query?type=history.
        var caller = await AuthedCaller();
        var client = caller.Client;
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var qsvc = _factory.Services.GetRequiredService<QueryService>();

        var sn = $"sphist{Guid.NewGuid():N}"[..12];
        await spaces.UpsertAsync(new Dmart.Models.Core.Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn, SpaceName = sn, Subpath = "/",
            OwnerShortname = _factory.AdminShortname,
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            // Update via /managed/request — Space uses its own shortname as
            // both space_name AND the Records[0].Shortname (self-referential).
            var resp = await PostRequest(client, RequestType.Update, sn, new Record
            {
                ResourceType = ResourceType.Space,
                Subpath = "/",
                Shortname = sn,
                Attributes = new()
                {
                    ["primary_website"] = "https://example.test",
                    ["icon"] = "settings",
                },
            });
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var hist = await QueryHistory(qsvc, sn, "/", sn);
            hist.Records!.Count.ShouldBe(1);
            var diff = (JsonElement)hist.Records[0].Attributes!["diff"]!;
            diff.TryGetProperty("primary_website", out var pwDiff).ShouldBeTrue();
            pwDiff.GetProperty("new").GetString().ShouldBe("https://example.test");
            diff.TryGetProperty("icon", out var iconDiff).ShouldBeTrue();
            iconDiff.GetProperty("new").GetString().ShouldBe("settings");
            hist.Records[0].Attributes!["owner_shortname"]!.ToString()
                .ShouldBe(caller.Shortname);
        }
        finally
        {
            try { await spaces.DeleteAsync(sn); } catch { }
        }
    }

    // ==================== helpers ====================

    // Returns the full TestUser (Client + Shortname) so tests can pin the
    // `owner_shortname` column on emitted history rows against the calling
    // user. Audit attribution is the load-bearing security claim for these
    // tests — asserting only on `diff` would pass even if the dispatcher
    // recorded the target shortname instead of the admin caller.
    private Task<DmartFactory.TestUser> AuthedCaller() =>
        _factory.CreateLoggedInUserAsync();

    private static async Task CreateRecord(HttpClient client, ResourceType rt, string subpath, string shortname,
                                            Dictionary<string, object> attrs)
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

    private async Task<Response> QueryHistory(QueryService qsvc, string space, string subpath, string shortname)
        => await qsvc.ExecuteAsync(new Query
        {
            Type = QueryType.History,
            SpaceName = space,
            Subpath = subpath,
            FilterShortnames = new() { shortname },
            Limit = 100,
        }, _factory.AdminShortname);
}
