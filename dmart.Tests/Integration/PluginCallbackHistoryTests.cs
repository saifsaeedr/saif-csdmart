using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins.Native;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the audit side-effect that native plugins now produce when they call
// back into save_entry / update_user:
//   - prior present + non-empty diff → exactly one histories row with the
//     {field: {old, new}} shape, owner = plugin-context actor
//   - prior present + empty diff (idempotent re-save) → zero rows
//   - prior absent (create path) → zero rows (Python parity with the REST
//     EntryService.UpdateAsync which only writes history on update)
//
// Calls into EmitSaveEntry / EmitUpdateUser directly so we exercise the
// typed-input path without needing to synthesise UTF-8 byte buffers (mirrors
// the LogCb → EmitPluginLog test split used in PluginLogToJsonlTests).
//
// [Collection] join: PluginInvocationContext.CurrentShortname/CurrentActor are
// ThreadStatic. Any other class that mutates them must join the same
// collection so xUnit runs them sequentially (see PluginInvocationContextCollection).
[Collection(PluginInvocationContextCollection.Name)]
public sealed class PluginCallbackHistoryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PluginCallbackHistoryTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task SaveEntry_Update_Writes_History_With_Diff_And_Plugin_Marker()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var spaceName = "cbhist_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = _factory.AdminShortname,
            IsActive = true, Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var sn = "t_" + Guid.NewGuid().ToString("N")[..6];
        var original = new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = sn, SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Ticket, IsActive = true,
            OwnerShortname = _factory.AdminShortname, State = "new",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await entryRepo.UpsertAsync(original);

        // Swap in the host services so the callback can resolve repositories.
        // Set the ambient plugin context the dispatcher would set on a real
        // hook invocation.
        // NativePluginCallbacks.Services is already wired to this same SP by
        // Program.cs:1347 at factory boot — don't overwrite it here, and
        // crucially don't null it in finally (that would poison cross-class
        // state for any later test that triggers a native plugin hook).
        PluginInvocationContext.CurrentShortname = "test_plugin";
        PluginInvocationContext.CurrentActor = _factory.AdminShortname;
        try
        {
            // Mutate state — should produce exactly one history row.
            var mutated = original with { State = "confirmed", UpdatedAt = DateTime.UtcNow };
            NativePluginCallbacks.EmitSaveEntry(mutated, logger: null).ShouldBe(0);

            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = spaceName,
                Subpath = "/",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();
            resp.Records!.Count.ShouldBe(1);

            var attrs = resp.Records[0].Attributes!;
            attrs["owner_shortname"].ToString().ShouldBe(_factory.AdminShortname);
            var diff = (JsonElement)attrs["diff"]!;
            diff.TryGetProperty("state", out var stateDiff).ShouldBeTrue();
            stateDiff.GetProperty("old").GetString().ShouldBe("new");
            stateDiff.GetProperty("new").GetString().ShouldBe("confirmed");

            // request_headers is stripped from the QueryService response (Python
            // parity), so inspect the raw row to verify the plugin marker.
            var headers = await ReadHeadersAsync(sp, spaceName, "/", sn);
            headers.GetProperty("x-plugin").GetString().ShouldBe("test_plugin");
            headers.GetProperty("x-source").GetString().ShouldBe("plugin");
        }
        finally
        {
            PluginInvocationContext.CurrentShortname = null;
            PluginInvocationContext.CurrentActor = null;
            try { await entryRepo.DeleteAsync(spaceName, "/", sn, ResourceType.Ticket); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
        }
    }

    [FactIfPg]
    public async Task SaveEntry_Idempotent_Resave_Does_Not_Write_History()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var spaceName = "cbhist_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = _factory.AdminShortname,
            IsActive = true, Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var sn = "t_" + Guid.NewGuid().ToString("N")[..6];
        var original = new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = sn, SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Ticket, IsActive = true,
            OwnerShortname = _factory.AdminShortname, State = "new",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await entryRepo.UpsertAsync(original);

        // NativePluginCallbacks.Services is already wired to this same SP by
        // Program.cs:1347 at factory boot — don't overwrite it here, and
        // crucially don't null it in finally (that would poison cross-class
        // state for any later test that triggers a native plugin hook).
        PluginInvocationContext.CurrentShortname = "test_plugin";
        PluginInvocationContext.CurrentActor = _factory.AdminShortname;
        try
        {
            // Re-save the same entry — diff is empty, no history row expected.
            NativePluginCallbacks.EmitSaveEntry(original, logger: null).ShouldBe(0);

            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = spaceName,
                Subpath = "/",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);
            resp.Records!.Count.ShouldBe(0);
        }
        finally
        {
            PluginInvocationContext.CurrentShortname = null;
            PluginInvocationContext.CurrentActor = null;
            try { await entryRepo.DeleteAsync(spaceName, "/", sn, ResourceType.Ticket); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
        }
    }

    [FactIfPg]
    public async Task SaveEntry_Create_Path_Does_Not_Write_History()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var spaceName = "cbhist_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = _factory.AdminShortname,
            IsActive = true, Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var sn = "t_" + Guid.NewGuid().ToString("N")[..6];
        var fresh = new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = sn, SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Ticket, IsActive = true,
            OwnerShortname = _factory.AdminShortname, State = "new",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };

        // NativePluginCallbacks.Services is already wired to this same SP by
        // Program.cs:1347 at factory boot — don't overwrite it here, and
        // crucially don't null it in finally (that would poison cross-class
        // state for any later test that triggers a native plugin hook).
        PluginInvocationContext.CurrentShortname = "test_plugin";
        PluginInvocationContext.CurrentActor = _factory.AdminShortname;
        try
        {
            // First time — no prior row exists. Should insert and skip
            // history (Python parity: create writes no history row).
            NativePluginCallbacks.EmitSaveEntry(fresh, logger: null).ShouldBe(0);

            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = spaceName,
                Subpath = "/",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);
            resp.Records!.Count.ShouldBe(0);
        }
        finally
        {
            PluginInvocationContext.CurrentShortname = null;
            PluginInvocationContext.CurrentActor = null;
            try { await entryRepo.DeleteAsync(spaceName, "/", sn, ResourceType.Ticket); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
        }
    }

    [FactIfPg]
    public async Task UpdateUser_Update_Writes_History_With_Diff_And_Plugin_Marker()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var sn = "cbu_" + Guid.NewGuid().ToString("N")[..10];
        var original = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn,
            SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true,
            Email = $"{sn}@test.local", IsEmailVerified = true,
            Roles = new(), Groups = new(),
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(original);

        // NativePluginCallbacks.Services is already wired to this same SP by
        // Program.cs:1347 at factory boot — don't overwrite it here, and
        // crucially don't null it in finally (that would poison cross-class
        // state for any later test that triggers a native plugin hook).
        PluginInvocationContext.CurrentShortname = "test_plugin";
        PluginInvocationContext.CurrentActor = _factory.AdminShortname;
        try
        {
            // Flip email + language → expect both in the diff, with language
            // in wire form ("english"/"arabic").
            var mutated = original with
            {
                Email = $"new_{sn}@test.local",
                Language = Language.Ar,
                UpdatedAt = DateTime.UtcNow,
            };
            NativePluginCallbacks.EmitUpdateUser(mutated, logger: null).ShouldBe(0);

            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = "management",
                Subpath = "/users",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);

            resp.Status.ShouldBe(Status.Success);
            resp.Records!.Count.ShouldBe(1);

            var attrs = resp.Records[0].Attributes!;
            attrs["owner_shortname"].ToString().ShouldBe(_factory.AdminShortname);
            var diff = (JsonElement)attrs["diff"]!;
            diff.TryGetProperty("email", out var emailDiff).ShouldBeTrue();
            emailDiff.GetProperty("new").GetString().ShouldBe($"new_{sn}@test.local");
            diff.TryGetProperty("language", out var langDiff).ShouldBeTrue();
            langDiff.GetProperty("old").GetString().ShouldBe("english");
            langDiff.GetProperty("new").GetString().ShouldBe("arabic");

            var headers = await ReadHeadersAsync(sp, "management", "/users", sn);
            headers.GetProperty("x-plugin").GetString().ShouldBe("test_plugin");
        }
        finally
        {
            PluginInvocationContext.CurrentShortname = null;
            PluginInvocationContext.CurrentActor = null;
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task UpdateUser_Idempotent_Resave_Does_Not_Write_History()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var sn = "cbu_" + Guid.NewGuid().ToString("N")[..10];
        var original = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn,
            SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true,
            Email = $"{sn}@test.local", IsEmailVerified = true,
            Roles = new(), Groups = new(),
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(original);

        PluginInvocationContext.CurrentShortname = "test_plugin";
        PluginInvocationContext.CurrentActor = _factory.AdminShortname;
        try
        {
            // Re-save the same user — diff is empty, no history row expected.
            NativePluginCallbacks.EmitUpdateUser(original, logger: null).ShouldBe(0);

            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = "management",
                Subpath = "/users",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);
            resp.Records!.Count.ShouldBe(0);
        }
        finally
        {
            PluginInvocationContext.CurrentShortname = null;
            PluginInvocationContext.CurrentActor = null;
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task UpdateUser_Create_Path_Does_Not_Write_History()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var qsvc = sp.GetRequiredService<QueryService>();

        // Brand-new shortname that doesn't exist yet — EmitUpdateUser should
        // insert (inserted == true → skip history). Confirms the create-side
        // of the inserted-vs-update predicate for users, parallel to
        // SaveEntry_Create_Path_Does_Not_Write_History.
        var sn = "cbu_" + Guid.NewGuid().ToString("N")[..10];
        var fresh = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn,
            SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true,
            Email = $"{sn}@test.local", IsEmailVerified = true,
            Roles = new(), Groups = new(),
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };

        PluginInvocationContext.CurrentShortname = "test_plugin";
        PluginInvocationContext.CurrentActor = _factory.AdminShortname;
        try
        {
            NativePluginCallbacks.EmitUpdateUser(fresh, logger: null).ShouldBe(0);

            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = "management",
                Subpath = "/users",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);
            resp.Records!.Count.ShouldBe(0);
        }
        finally
        {
            PluginInvocationContext.CurrentShortname = null;
            PluginInvocationContext.CurrentActor = null;
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    // Direct DB read — QueryService strips request_headers from history
    // records on the way out (Python parity), so we can't observe the plugin
    // marker through the public response shape.
    private static async Task<JsonElement> ReadHeadersAsync(
        IServiceProvider sp, string spaceName, string subpath, string shortname)
    {
        var db = sp.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("""
            SELECT request_headers FROM histories
            WHERE space_name = $1 AND subpath = $2 AND shortname = $3
            ORDER BY timestamp DESC LIMIT 1
            """, conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        var raw = (string?)await cmd.ExecuteScalarAsync();
        raw.ShouldNotBeNull();
        return JsonDocument.Parse(raw!).RootElement;
    }
}
