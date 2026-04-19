using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end tests for the access-control features that PermissionService gained when
// the C# port was brought to dmart-Python parity. Each test:
//
//   1. Pulls PermissionService + the supporting repositories from the DI container.
//   2. Writes a real Permission/Role/User row to the live Postgres DB.
//   3. Calls CanAsync directly (bypassing HTTP) to check the gating logic.
//   4. Cleans up after itself so the DB stays usable for other tests.
//
// Each scenario uses a unique user shortname so parallel runs don't collide.
public class PermissionServiceIntegrationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PermissionServiceIntegrationTests(DmartFactory factory) => _factory = factory;

    // ----- harness -----

    private (PermissionService perms, UserRepository users, AccessRepository access)
        Resolve()
    {
        // Force the host to construct so hosted services run (AdminBootstrap creates
        // super_manager + super_admin, SchemaInitializer ensures tables).
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<PermissionService>(),
            sp.GetRequiredService<UserRepository>(),
            sp.GetRequiredService<AccessRepository>());
    }

    private static async Task CleanupUserAsync(
        UserRepository users, AccessRepository access,
        string user, string role, string perm)
    {
        // Best-effort tear-down; ignore failures so test rerun is idempotent.
        try { await users.DeleteAsync(user); } catch { }
        // Roles/permissions don't have a clean delete API in the C# port — leaving
        // them in place is harmless because they're scoped by unique shortname and
        // any next test re-upserts.
        await access.InvalidateAllCachesAsync();
        await Task.Yield();
        _ = role; _ = perm;
    }

    private static Permission BuildPerm(
        string shortname,
        string spaceName,
        Dictionary<string, List<string>> subpaths,
        List<string> actions,
        List<string>? resourceTypes = null,
        List<string>? conditions = null,
        List<string>? restrictedFields = null,
        Dictionary<string, object>? allowedFieldsValues = null)
    => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "permissions",
        OwnerShortname = "dmart",
        IsActive = true,
        Subpaths = subpaths,
        Actions = actions,
        ResourceTypes = resourceTypes ?? new(),
        Conditions = conditions ?? new(),
        RestrictedFields = restrictedFields,
        AllowedFieldsValues = allowedFieldsValues,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Role BuildRole(string shortname, params string[] permissions)
    => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "roles",
        OwnerShortname = "dmart",
        IsActive = true,
        Permissions = new(permissions),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static User BuildUser(string shortname, params string[] roles)
    => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "users",
        OwnerShortname = shortname,
        IsActive = true,
        Roles = new(roles),
        Type = UserType.Web,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ==================== test cases ====================

    [Fact]
    public async Task Hierarchical_Walk_Permission_On_Parent_Subpath_Grants_Access_To_Child()
    {
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var permName = $"itest_perm_walk_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_walk_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_walk_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            // Permission grants "view" on subpath "projects" in the test space.
            // The C# walk produces "/", "projects", "projects/foo", "projects/foo/bar"
            // for /projects/foo/bar — so a permission keyed on the literal "projects"
            // entry must be hit at iteration 2 of the walk.
            await access.UpsertPermissionAsync(BuildPerm(
                permName,
                "test",
                new() { ["test"] = new() { "projects" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));

            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            // Direct hit on the parent subpath
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/projects", "x")))
                .ShouldBeTrue("permission on 'projects' should grant access to /projects");

            // Hierarchical walk: child subpath should also pass via the parent permission
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/projects/foo/bar", "x")))
                .ShouldBeTrue("permission on 'projects' should grant access to /projects/foo/bar via the walk");

            // Sibling subpath should be denied — "other" is not in the walk of /projects
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/other", "x")))
                .ShouldBeFalse("permission on 'projects' must NOT grant access to /other");
        }
        finally
        {
            await CleanupUserAsync(users, access, userName, roleName, permName);
        }
    }

    [Fact]
    public async Task All_Subpaths_Magic_Word_Grants_Access_Across_Space()
    {
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var permName = $"itest_perm_mw_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_mw_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_mw_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            // {"test": ["__all_subpaths__"]} — should grant access to anything in `test`.
            await access.UpsertPermissionAsync(BuildPerm(
                permName,
                "test",
                new() { ["test"] = new() { PermissionService.AllSubpathsMw } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));

            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            // Any deep subpath in `test` should pass via the global-form rewrite.
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/anything/deep/here", "x")))
                .ShouldBeTrue();

            // But NOT in another space.
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "other_space", "/", "x")))
                .ShouldBeFalse();
        }
        finally
        {
            await CleanupUserAsync(users, access, userName, roleName, permName);
        }
    }

    [Fact]
    public async Task All_Spaces_Magic_Word_Grants_Access_Across_Spaces()
    {
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var permName = $"itest_perm_as_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_as_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_as_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            // __all_spaces__:__all_subpaths__ — the form super_manager itself uses.
            await access.UpsertPermissionAsync(BuildPerm(
                permName,
                "management",
                new()
                {
                    [PermissionService.AllSpacesMw] = new() { PermissionService.AllSubpathsMw },
                },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));

            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "anything_at_all", "/wherever", "x")))
                .ShouldBeTrue();
        }
        finally
        {
            await CleanupUserAsync(users, access, userName, roleName, permName);
        }
    }

    [Fact]
    public async Task Per_Entry_Acl_Grant_Overrides_Missing_Role_Permission()
    {
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var roleName = $"itest_role_acl_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_acl_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            // Role has NO permissions at all. Without the per-entry ACL grant, every
            // CanAsync should return false.
            await access.UpsertRoleAsync(BuildRole(roleName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/things", "thing1");

            // No ACL → denied (no role permissions).
            (await perms.CanReadAsync(userName, locator)).ShouldBeFalse();

            // With an ACL grant naming this user → allowed.
            var ctx = new PermissionService.ResourceContext(
                IsActive: true,
                OwnerShortname: "someone_else",
                OwnerGroupShortname: null,
                Acl: new()
                {
                    new AclEntry { UserShortname = userName, Allowed = new() { "view" } },
                });
            (await perms.CanReadAsync(userName, locator, ctx)).ShouldBeTrue();

            // ACL with a different user → still denied.
            var ctxWrongUser = ctx with
            {
                Acl = new() { new AclEntry { UserShortname = "other_user", Allowed = new() { "view" } } },
            };
            (await perms.CanReadAsync(userName, locator, ctxWrongUser)).ShouldBeFalse();
        }
        finally
        {
            try { await users.DeleteAsync(userName); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [Fact]
    public async Task Own_Condition_Allows_Owner_Update_Denies_Stranger()
    {
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var permName = $"itest_perm_own_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_own_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_own_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            await access.UpsertPermissionAsync(BuildPerm(
                permName,
                "management",
                new() { ["test"] = new() { "items" } },
                actions: new() { "update" },
                resourceTypes: new() { "content" },
                conditions: new() { "own" }));

            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "thing");

            // Resource owned by the user → "own" condition achieved → allowed.
            var ownCtx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, ownCtx, null))
                .ShouldBeTrue("user is the owner — own condition met");

            // Resource owned by someone else → not "own" → denied.
            var strangerCtx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: "another_user", OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, strangerCtx, null))
                .ShouldBeFalse("user is not the owner — own condition unmet");
        }
        finally
        {
            try { await users.DeleteAsync(userName); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [Fact]
    public async Task Restricted_Field_Blocks_Update_Touching_That_Field()
    {
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var permName = $"itest_perm_rf_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_rf_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_rf_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            await access.UpsertPermissionAsync(BuildPerm(
                permName,
                "management",
                new() { ["test"] = new() { "tickets" } },
                actions: new() { "update" },
                resourceTypes: new() { "content" },
                restrictedFields: new() { "status", "payload.body.amount" }));

            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/tickets", "t1");
            var ctx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);

            // Patch touching only "title" — allowed
            (await perms.CanUpdateAsync(userName, locator, ctx,
                new Dictionary<string, object> { ["title"] = "renamed" }))
                .ShouldBeTrue("untouched fields are fine");

            // Patch touching "status" directly — blocked
            (await perms.CanUpdateAsync(userName, locator, ctx,
                new Dictionary<string, object> { ["status"] = "closed" }))
                .ShouldBeFalse("status is restricted");

            // Patch touching nested "payload.body.amount" via dot prefix — blocked
            (await perms.CanUpdateAsync(userName, locator, ctx,
                new Dictionary<string, object>
                {
                    ["payload"] = new Dictionary<string, object>
                    {
                        ["body"] = new Dictionary<string, object> { ["amount"] = 99 },
                    },
                }))
                .ShouldBeFalse("nested payload.body.amount is restricted via prefix match");
        }
        finally
        {
            try { await users.DeleteAsync(userName); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // Python-parity anonymous access: for any grant to take effect,
    // `adapter.generate_user_permissions` requires (a) an "anonymous" user row,
    // (b) at least one role on that user, and (c) a "world" permission (or the
    // role's own permissions covering the target). No shortcut grants anonymous
    // any implicit access — a bare null actor resolves to zero permissions.
    [Fact]
    public async Task Anonymous_With_World_Permission_Can_View_But_Cannot_Write()
    {
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var anonRole = $"itest_anon_role_{Guid.NewGuid():N}".Substring(0, 24);
        const string worldPerm = "world";      // Python-reserved shortname
        const string anonUser = "anonymous";   // Python-reserved shortname

        // Preserve any pre-existing anonymous user/world permission so we
        // restore them on teardown — otherwise a shared DB loses them.
        var priorAnon = await users.GetByShortnameAsync(anonUser);
        var priorWorld = await access.GetPermissionAsync(worldPerm);

        try
        {
            await access.UpsertPermissionAsync(BuildPerm(
                worldPerm,
                "management",
                new() { [PermissionService.AllSpacesMw] = new() { PermissionService.AllSubpathsMw } },
                actions: new() { "view", "query" },
                resourceTypes: new() { "content" }));

            await access.UpsertRoleAsync(BuildRole(anonRole, worldPerm));
            await users.UpsertAsync(BuildUser(anonUser, anonRole));
            await access.InvalidateAllCachesAsync();

            var l = new Locator(ResourceType.Content, "any", "/", "x");
            (await perms.CanReadAsync(null, l)).ShouldBeTrue("anonymous read via world");
            (await perms.CanCreateAsync(null, l)).ShouldBeFalse("anonymous create denied (no 'create' action)");
            (await perms.CanUpdateAsync(null, l)).ShouldBeFalse("anonymous update denied");
            (await perms.CanDeleteAsync(null, l)).ShouldBeFalse("anonymous delete denied");
        }
        finally
        {
            try { await users.DeleteAsync(anonUser); } catch { }
            try { await access.DeleteRoleAsync(anonRole); } catch { }
            try { await access.DeletePermissionAsync(worldPerm); } catch { }
            // Restore any prior state we displaced.
            if (priorAnon is not null)  await users.UpsertAsync(priorAnon);
            if (priorWorld is not null) await access.UpsertPermissionAsync(priorWorld);
            await access.InvalidateAllCachesAsync();
        }
    }

    [Fact]
    public async Task Anonymous_Without_Roles_Has_No_Access_Even_If_World_Exists()
    {
        // Python parity: world is only consulted INSIDE the role loop. An
        // anonymous user with zero roles never hits world, so a world-granting
        // deployment that forgets to link a role to anonymous grants nothing.
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();
        const string worldPerm = "world";
        const string anonUser = "anonymous";
        var priorAnon = await users.GetByShortnameAsync(anonUser);
        var priorWorld = await access.GetPermissionAsync(worldPerm);
        try
        {
            await access.UpsertPermissionAsync(BuildPerm(
                worldPerm,
                "management",
                new() { [PermissionService.AllSpacesMw] = new() { PermissionService.AllSubpathsMw } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            // Anonymous user with NO roles — mirrors "forgot to link" deployments.
            await users.UpsertAsync(BuildUser(anonUser /* no roles */));
            await access.InvalidateAllCachesAsync();

            var l = new Locator(ResourceType.Content, "any", "/", "x");
            (await perms.CanReadAsync(null, l)).ShouldBeFalse(
                "anonymous with zero roles must NOT see world grants — Python parity");
        }
        finally
        {
            try { await users.DeleteAsync(anonUser); } catch { }
            try { await access.DeletePermissionAsync(worldPerm); } catch { }
            if (priorAnon is not null)  await users.UpsertAsync(priorAnon);
            if (priorWorld is not null) await access.UpsertPermissionAsync(priorWorld);
            await access.InvalidateAllCachesAsync();
        }
    }

    [Fact]
    public async Task Anonymous_Without_World_Or_Anonymous_User_Has_No_Access()
    {
        // Vanilla deployment with no anonymous-user row AND no world permission:
        // bare null actor resolves to zero permissions. No read, no write.
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        // Guarantee a clean slate — remove any stray anonymous/world rows.
        var priorAnon = await users.GetByShortnameAsync("anonymous");
        var priorWorld = await access.GetPermissionAsync("world");
        if (priorAnon is not null) await users.DeleteAsync("anonymous");
        if (priorWorld is not null) await access.DeletePermissionAsync("world");
        await access.InvalidateAllCachesAsync();

        try
        {
            var l = new Locator(ResourceType.Content, "any", "/", "x");
            (await perms.CanReadAsync(null, l)).ShouldBeFalse("no anon/world → no access");
            (await perms.CanCreateAsync(null, l)).ShouldBeFalse();
            (await perms.CanUpdateAsync(null, l)).ShouldBeFalse();
            (await perms.CanDeleteAsync(null, l)).ShouldBeFalse();
        }
        finally
        {
            if (priorAnon is not null)  await users.UpsertAsync(priorAnon);
            if (priorWorld is not null) await access.UpsertPermissionAsync(priorWorld);
            await access.InvalidateAllCachesAsync();
        }
    }

    // ==================== folder walk: shortname appended to subpath ====================

    [Fact]
    public async Task Folder_Walk_Permission_On_Subpath_Grants_Access_To_Folder_Entry_At_Root()
    {
        // Python appends the folder shortname to the subpath walk. So a permission
        // on subpath "users" grants access to the folder entry {subpath="/", shortname="users"}.
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var permName = $"itest_perm_fw_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_fw_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_fw_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            // Permission grants view on subpath "users" with resource_type "folder".
            await access.UpsertPermissionAsync(BuildPerm(
                permName,
                "management",
                new() { ["test"] = new() { "users" } },
                actions: new() { "view", "query" },
                resourceTypes: new() { "folder", "user" }));

            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            // Folder entry at subpath="/", shortname="users" — the walk appends "users"
            // to produce ["/", "users"], and "users" matches the permission.
            var folderLocator = new Locator(ResourceType.Folder, "test", "/", "users");
            (await perms.CanReadAsync(userName, folderLocator))
                .ShouldBeTrue("permission on subpath 'users' should grant view of folder 'users' at /");

            // Content inside the users folder — subpath is "users", walk hits directly.
            var contentLocator = new Locator(ResourceType.User, "test", "/users", "alice");
            (await perms.CanReadAsync(userName, contentLocator))
                .ShouldBeTrue("permission on subpath 'users' should grant view of entries under /users");

            // Folder at a different subpath — should be denied.
            var otherFolder = new Locator(ResourceType.Folder, "test", "/", "roles");
            (await perms.CanReadAsync(userName, otherFolder))
                .ShouldBeFalse("no permission on 'roles' — folder should be denied");
        }
        finally
        {
            await CleanupUserAsync(users, access, userName, roleName, permName);
        }
    }

    // ==================== effective_space for space resources ====================

    [Fact]
    public async Task Space_Resource_Uses_Shortname_As_Effective_Space()
    {
        // Python: effective_space = entry_shortname when resource_type == space.
        // A permission keyed to space "test" should grant query access when checking
        // a Space resource whose shortname is "test" (even though the locator's
        // space_name is "management" — the management space is where spaces live).
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var permName = $"itest_perm_es_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_es_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_es_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            // Permission grants "query" on any subpath in the "test" space.
            // Empty resourceTypes means "any resource type" — matching how real
            // permissions work (they rarely include "space" explicitly).
            await access.UpsertPermissionAsync(BuildPerm(
                permName,
                "management",
                new() { ["test"] = new() { PermissionService.AllSubpathsMw } },
                actions: new() { "query", "view" }));

            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            // Check: can the user query the Space resource for "test"?
            // The locator has space_name="management" (spaces live there) but
            // shortname="test" — effective_space should resolve to "test".
            var spaceLocator = new Locator(ResourceType.Space, "management", "/", "test");
            (await perms.CanAsync(userName, "query", spaceLocator))
                .ShouldBeTrue("permission on space 'test' should grant query on Space resource 'test'");

            // Space "other" should be denied — no permission for it.
            var otherSpace = new Locator(ResourceType.Space, "management", "/", "other");
            (await perms.CanAsync(userName, "query", otherSpace))
                .ShouldBeFalse("no permission for space 'other'");
        }
        finally
        {
            await CleanupUserAsync(users, access, userName, roleName, permName);
        }
    }

    // ==================== HasAnyAccessToSpaceAsync ====================

    [Fact]
    public async Task HasAnyAccess_Returns_True_For_Space_With_Any_Permission()
    {
        if (!DmartFactory.HasPg) return;
        var (perms, users, access) = Resolve();

        var permName = $"itest_perm_ha_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_ha_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_ha_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            // Permission only covers "test" space with resource_type "content" — no "space".
            await access.UpsertPermissionAsync(BuildPerm(
                permName,
                "management",
                new() { ["test"] = new() { "data" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));

            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await users.UpsertAsync(BuildUser(userName, roleName));
            await access.InvalidateAllCachesAsync();

            // HasAnyAccessToSpaceAsync should return true for "test" (has a permission referencing it).
            (await perms.HasAnyAccessToSpaceAsync(userName, "test"))
                .ShouldBeTrue("user has content permission in 'test' space");

            // Should return false for unrelated space.
            (await perms.HasAnyAccessToSpaceAsync(userName, "unrelated"))
                .ShouldBeFalse("no permission in 'unrelated' space");

            // Anonymous should always return false.
            (await perms.HasAnyAccessToSpaceAsync(null, "test"))
                .ShouldBeFalse("anonymous has no space access");
        }
        finally
        {
            await CleanupUserAsync(users, access, userName, roleName, permName);
        }
    }
}
