using System.Net;
using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

public sealed class ForceDeleteRepoTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ForceDeleteRepoTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task DeleteFolderTree_Reports_Entry_Count_For_Folder_And_Children()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
        var repo = _factory.Services.GetRequiredService<EntryRepository>();
        var space = "test";
        var folder = $"f{Guid.NewGuid():N}"[..12];

        async Task Create(ResourceType rt, string subpath, string sn) =>
            (await client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Create, SpaceName = space,
                Records = new() { new Record { ResourceType = rt, Subpath = subpath, Shortname = sn } },
            }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        await Create(ResourceType.Folder, "/", folder);
        await Create(ResourceType.Content, $"/{folder}", "c1");

        var report = await repo.DeleteFolderTreeWithDependentsAsync(space, "/", folder);

        report.Entries.ShouldBe(2); // the folder row + its one child
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task DeleteAsync_NonEmptyFolder_NoForce_Fails()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
        var svc = _factory.Services.GetRequiredService<Dmart.Services.EntryService>();
        var space = "test";
        var folder = $"f{Guid.NewGuid():N}"[..12];

        async Task Create(ResourceType rt, string subpath, string sn) =>
            (await client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Create, SpaceName = space,
                Records = new() { new Record { ResourceType = rt, Subpath = subpath, Shortname = sn } },
            }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        await Create(ResourceType.Folder, "/", folder);
        await Create(ResourceType.Content, $"/{folder}", "c1");

        var locator = new Locator(ResourceType.Folder, space, "/", folder);
        var res = await svc.DeleteAsync(locator, caller.Shortname, force: false);
        res.IsOk.ShouldBeFalse();
        res.ErrorCode.ShouldBe(Dmart.Models.Api.InternalErrorCode.CANNT_DELETE);

        // force=true succeeds and reports refs
        var forced = await svc.DeleteAsync(locator, caller.Shortname, force: true);
        forced.IsOk.ShouldBeTrue();
        forced.Value!.Entries.ShouldBe(2); // folder + c1
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task DeleteAsync_EmptyFolder_NoForce_Succeeds()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
        var svc = _factory.Services.GetRequiredService<Dmart.Services.EntryService>();
        var space = "test";
        var folder = $"f{Guid.NewGuid():N}"[..12];
        (await client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = space,
            Records = new() { new Record { ResourceType = ResourceType.Folder, Subpath = "/", Shortname = folder } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        var res = await svc.DeleteAsync(new Locator(ResourceType.Folder, space, "/", folder), caller.Shortname, force: false);
        res.IsOk.ShouldBeTrue();
        res.Value!.Entries.ShouldBe(1); // just the (empty) folder row
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task OwnsAnyRecords_True_When_User_Created_Entry()
    {
        var owner = await _factory.CreateLoggedInUserAsync();   // logged-in user owns nothing yet
        var users = _factory.Services.GetRequiredService<UserRepository>();
        (await users.OwnsAnyRecordsAsync(owner.Shortname)).ShouldBeFalse();

        (await owner.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest",
                Shortname = $"o{Guid.NewGuid():N}"[..12] } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        (await users.OwnsAnyRecordsAsync(owner.Shortname)).ShouldBeTrue();
        await owner.Cleanup();
    }

    [FactIfPg]
    public async Task OwnsAnyRecords_True_When_User_Owns_Another_User()
    {
        // A user who owns ONLY other users must still be steered onto the force
        // path: the plain delete does no reassignment or query_policies regen, so
        // an owned user would be left dangling on a reusable shortname.
        var owner = await _factory.CreateLoggedInUserAsync();   // owns nothing yet
        var users = _factory.Services.GetRequiredService<UserRepository>();
        (await users.OwnsAnyRecordsAsync(owner.Shortname)).ShouldBeFalse();

        var ownedSn = $"u{Guid.NewGuid():N}"[..12];
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ownedSn, SpaceName = "management", Subpath = "/users",
            OwnerShortname = owner.Shortname, IsActive = true,
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        try
        {
            (await users.OwnsAnyRecordsAsync(owner.Shortname)).ShouldBeTrue();
            // A self-owned user must NOT count, or no user could ever plain-delete.
            (await users.OwnsAnyRecordsAsync(ownedSn)).ShouldBeFalse();
        }
        finally { try { await users.DeleteAsync(ownedSn); } catch { } }
        await owner.Cleanup();
    }

    [FactIfPg]
    public async Task OwnsSpaceAsync_Returns_True_For_Owned_Space_And_False_For_Unknown()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var spaceName = $"itest_owns_{Guid.NewGuid():N}"[..16];

        try
        {
            // False path — space does not exist yet.
            (await users.OwnsSpaceAsync(caller.Shortname, "no_such_space_xyz")).ShouldBeFalse();

            // Create a space owned by the logged-in test user.
            var createReq = new Request
            {
                RequestType = RequestType.Create,
                SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Space,
                        Subpath = "/",
                        Shortname = spaceName,
                        Attributes = new() { ["is_active"] = true },
                    },
                },
            };
            var resp = await caller.Client.PostAsJsonAsync("/managed/request", createReq, DmartJsonContext.Default.Request);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

            // True path — caller now owns that space.
            (await users.OwnsSpaceAsync(caller.Shortname, spaceName)).ShouldBeTrue();

            // False path again — a different (non-existent) space name still returns false.
            (await users.OwnsSpaceAsync(caller.Shortname, "no_such_space_xyz")).ShouldBeFalse();
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
            await caller.Cleanup();
        }
    }

    [FactIfPg]
    public async Task ForceDelete_Removes_User_And_Owned_Entries()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var sn = $"e{Guid.NewGuid():N}"[..12];
        (await owner.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest", Shortname = sn } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        var report = await users.ForceDeleteAsync(owner.Shortname);

        (await users.GetByShortnameAsync(owner.Shortname)).ShouldBeNull();
        report.Entries.ShouldBe(1); // the one content entry the user owned
        // entry is gone — owner.Client returns 401 after user deletion (auth middleware
        // rejects requests for non-existent users, see FullParityTests), so verify via repo.
        (await entries.GetAsync("test", "/itest", sn, ResourceType.Content)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task ForceDelete_Removes_User_Sessions()
    {
        // A logged-in user has at least one live session row.
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        (await users.CountSessionsAsync(owner.Shortname)).ShouldBeGreaterThan(0);

        await users.ForceDeleteAsync(owner.Shortname);

        (await users.CountSessionsAsync(owner.Shortname)).ShouldBe(0);
        (await users.GetByShortnameAsync(owner.Shortname)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task ForceDelete_Removes_UserPermissionsCache()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        // Seed the resolved-permissions cache row for the user.
        await access.CacheUserPermissionsAsync(owner.Shortname, new Dictionary<string, object>());
        (await access.GetCachedUserPermissionsAsync(owner.Shortname)).ShouldNotBeNull();

        await users.ForceDeleteAsync(owner.Shortname);

        (await access.GetCachedUserPermissionsAsync(owner.Shortname)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task ForceDelete_Removes_Histories_For_Owned_Entries_In_Other_Spaces()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var history = _factory.Services.GetRequiredService<HistoryRepository>();
        var sn = $"e{Guid.NewGuid():N}"[..12];
        // The user owns an entry in "test", a space the user does NOT own — its
        // history rows are exactly the residue ForceDelete must now clear.
        (await owner.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest", Shortname = sn } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();
        await history.AppendAsync("test", "/itest", sn, owner.Shortname, null, null);
        (await history.ListAsync("test", "/itest", sn)).Count.ShouldBeGreaterThan(0);

        await users.ForceDeleteAsync(owner.Shortname);

        (await history.ListAsync("test", "/itest", sn)).Count.ShouldBe(0);
    }

    [FactIfPg]
    public async Task ForceDelete_Removes_Locks_For_Owned_Entries_In_Other_Spaces()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var locks = _factory.Services.GetRequiredService<LockRepository>();
        var sn = $"e{Guid.NewGuid():N}"[..12];
        (await owner.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest", Shortname = sn } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();
        (await locks.TryLockAsync("test", "/itest", sn, owner.Shortname, 300)).ShouldBeTrue();
        (await locks.GetLockerAsync("test", "/itest", sn, 300)).ShouldBe(owner.Shortname);

        await users.ForceDeleteAsync(owner.Shortname);

        (await locks.GetLockerAsync("test", "/itest", sn, 300)).ShouldBeNull();
    }

    // ---- ownership reassignment: structural objects are KEPT, owner reset to the
    //      "dmart" super_admin (bootstrap provisions it; the cascade upserts a
    //      placeholder only if it's somehow absent) ----

    [FactIfPg]
    public async Task ForceDelete_Reassigns_Owned_Space_To_Dmart_And_Keeps_Foreign_Entries()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var space = $"itest_sp_{Guid.NewGuid():N}"[..18];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space, SpaceName = space, Subpath = "/",
            OwnerShortname = owner.Shortname, IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        // A foreign-owned entry inside the user's space must survive the reassignment
        // — proving the space is reassigned, not wiped with its contents.
        var foreignSn = $"f{Guid.NewGuid():N}"[..12];
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = foreignSn, SpaceName = space, Subpath = "/",
            ResourceType = ResourceType.Content, OwnerShortname = "dmart", IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        try
        {
            await users.ForceDeleteAsync(owner.Shortname);

            var reassigned = await spaces.GetAsync(space);
            reassigned.ShouldNotBeNull();                       // space survived (not wiped)
            reassigned!.OwnerShortname.ShouldBe("dmart");   // owner reassigned
            // query_policies regenerated for the new owner — the deleted owner's
            // owner-scoped pattern must not linger (shortname-reuse safety).
            reassigned.QueryPolicies.ShouldContain(p => p.EndsWith(":dmart"));
            reassigned.QueryPolicies.ShouldNotContain(p => p.Contains(owner.Shortname));
            (await entries.GetAsync(space, "/", foreignSn, ResourceType.Content))
                .ShouldNotBeNull();                             // foreign contents kept
            (await users.GetByShortnameAsync(owner.Shortname)).ShouldBeNull();
            (await users.GetByShortnameAsync("dmart")).ShouldNotBeNull(); // reassignment target exists
        }
        finally
        {
            try { await entries.DeleteAsync(space, "/", foreignSn, ResourceType.Content); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
        }
    }

    [FactIfPg]
    public async Task ForceDelete_Reassigns_Owned_Role_To_Dmart()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var name = "rrm_" + Guid.NewGuid().ToString("N")[..8];
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = name, SpaceName = "management", Subpath = "/roles",
            OwnerShortname = owner.Shortname, IsActive = true, Permissions = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        try
        {
            await users.ForceDeleteAsync(owner.Shortname);

            var role = await access.GetRoleAsync(name);
            role.ShouldNotBeNull();                       // role survived
            role!.OwnerShortname.ShouldBe("dmart");   // owner reassigned
        }
        finally { try { await access.DeleteRoleAsync(name); } catch { } }
    }

    [FactIfPg]
    public async Task ForceDelete_Reassigns_Owned_Permission_To_Dmart()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var name = "prm_" + Guid.NewGuid().ToString("N")[..8];
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = name, SpaceName = "management", Subpath = "/permissions",
            OwnerShortname = owner.Shortname, IsActive = true,
            Subpaths = new() { ["management"] = new() { "/" } },
            ResourceTypes = new() { "content" }, Actions = new() { "view" },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        try
        {
            await users.ForceDeleteAsync(owner.Shortname);

            var perm = await access.GetPermissionAsync(name);
            perm.ShouldNotBeNull();                       // permission survived
            perm!.OwnerShortname.ShouldBe("dmart");   // owner reassigned
        }
        finally { try { await access.DeletePermissionAsync(name); } catch { } }
    }

    [FactIfPg]
    public async Task ForceDelete_Reassigns_Owned_Group_To_Dmart()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var name = "grm_" + Guid.NewGuid().ToString("N")[..8];
        await access.UpsertGroupAsync(new Group
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = name, SpaceName = "management", Subpath = "/groups",
            OwnerShortname = owner.Shortname, IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        try
        {
            await users.ForceDeleteAsync(owner.Shortname);

            var group = await access.GetGroupAsync(name);
            group.ShouldNotBeNull();                       // group survived
            group!.OwnerShortname.ShouldBe("dmart");   // owner reassigned
        }
        finally { try { await access.DeleteGroupAsync(name); } catch { } }
    }

    [FactIfPg]
    public async Task ForceDelete_Reassigns_Other_Owned_Users_To_Dmart()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        // A second user OWNED BY the victim — its owner must be reset, not deleted.
        var ownedSn = $"u{Guid.NewGuid():N}"[..12];
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ownedSn, SpaceName = "management", Subpath = "/users",
            OwnerShortname = owner.Shortname, IsActive = true,
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        try
        {
            await users.ForceDeleteAsync(owner.Shortname);

            var survivor = await users.GetByShortnameAsync(ownedSn);
            survivor.ShouldNotBeNull();                       // owned user survived
            survivor!.OwnerShortname.ShouldBe("dmart");   // owner reassigned
            (await users.GetByShortnameAsync(owner.Shortname)).ShouldBeNull();
        }
        finally { try { await users.DeleteAsync(ownedSn); } catch { } }
    }

    // ---- dryrun: counts are exact, but nothing is actually removed ----

    [FactIfPg]
    public async Task DeleteFolderTree_DryRun_Projects_Count_Without_Removing()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
        var repo = _factory.Services.GetRequiredService<EntryRepository>();
        var space = "test";
        var folder = $"f{Guid.NewGuid():N}"[..12];

        async Task Create(ResourceType rt, string subpath, string sn) =>
            (await client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Create, SpaceName = space,
                Records = new() { new Record { ResourceType = rt, Subpath = subpath, Shortname = sn } },
            }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        await Create(ResourceType.Folder, "/", folder);
        await Create(ResourceType.Content, $"/{folder}", "c1");

        var report = await repo.DeleteFolderTreeWithDependentsAsync(space, "/", folder, dryRun: true);

        report.Entries.ShouldBe(2);                                       // would remove folder + child
        (await repo.GetAsync(space, "/", folder, ResourceType.Folder)).ShouldNotBeNull();    // still there
        (await repo.GetAsync(space, $"/{folder}", "c1", ResourceType.Content)).ShouldNotBeNull();

        await repo.DeleteFolderTreeWithDependentsAsync(space, "/", folder); // real cleanup
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task ForceDelete_DryRun_Projects_Count_Without_Removing()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var sn = $"e{Guid.NewGuid():N}"[..12];
        (await owner.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest", Shortname = sn } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        var report = await users.ForceDeleteAsync(owner.Shortname, dryRun: true);

        report.Entries.ShouldBe(1);                                        // would remove the owned entry
        (await users.GetByShortnameAsync(owner.Shortname)).ShouldNotBeNull();           // user untouched
        (await entries.GetAsync("test", "/itest", sn, ResourceType.Content)).ShouldNotBeNull();
        await owner.Cleanup();
    }

    // A dryrun must perform ZERO structural mutation: it must not reassign the user's
    // owned objects to the "dmart" sentinel — the very operation that, on a real
    // force-delete, materialises/touches that sentinel row. The real path is COUNT-only
    // for a dryrun, so the sentinel branch never runs; this guards against a future
    // change that lets a dryrun fall through to the mutating path. The role's owner
    // staying the victim (NOT "dmart") is the observable proof the reassignment — and
    // hence the sentinel materialisation — was skipped.
    [FactIfPg]
    public async Task ForceDelete_DryRun_Does_Not_Reassign_Structural_Owner_To_Sentinel()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var roleName = "rdr_" + Guid.NewGuid().ToString("N")[..8];
        // A structural object owned by the victim — a real force-delete would reassign
        // its owner to "dmart"; a dryrun must leave it exactly as-is.
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = roleName, SpaceName = "management", Subpath = "/roles",
            OwnerShortname = owner.Shortname, IsActive = true, Permissions = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        // An owned data entry so the projection still has something to count.
        var sn = $"e{Guid.NewGuid():N}"[..12];
        (await owner.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest", Shortname = sn } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();
        try
        {
            var report = await users.ForceDeleteAsync(owner.Shortname, dryRun: true);

            report.Entries.ShouldBe(1);                                       // projection still works
            (await users.GetByShortnameAsync(owner.Shortname)).ShouldNotBeNull(); // victim untouched

            var role = await access.GetRoleAsync(roleName);
            role.ShouldNotBeNull();                                           // role untouched
            role!.OwnerShortname.ShouldBe(owner.Shortname);                   // NOT reassigned to "dmart"
        }
        finally
        {
            try { await access.DeleteRoleAsync(roleName); } catch { }
            await owner.Cleanup();
        }
    }
}
