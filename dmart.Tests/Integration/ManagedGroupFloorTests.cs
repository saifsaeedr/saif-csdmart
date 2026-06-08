using System.Net.Http.Json;
using System.Text;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Group counterpart to ManagedPrivilegeFloorTests. A group is now a first-class
// resource with its own grantable_by. The privilege floor enforces two rules for
// a NON-global-admin going through /managed/request:
//   1. a group may be ASSIGNED to a user only when the group's grantable_by lists
//      a group the actor already belongs to;
//   2. a group's grantable_by may not be SET at all (global-admin-only).
// These pin both ends of that contract end to end.
public class ManagedGroupFloorTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedGroupFloorTests(DmartFactory factory) => _factory = factory;

    // A permission granting `create` on `resourceType` across all spaces, wrapped
    // in a role. Enough to pass the CanCreate gate without being a global admin
    // (that needs create AND update over __all_spaces__/__all_subpaths__), so the
    // privilege floor — not the create gate — is what decides the outcome.
    private static async Task<(string role, string perm)> CreateCreatorRoleAsync(
        AccessRepository access, string suffix, string resourceType)
    {
        var permName = "gfloorperm_" + suffix;
        var roleName = "gfloorrole_" + suffix;
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = permName, SpaceName = "management", Subpath = "/permissions",
            OwnerShortname = "dmart", IsActive = true,
            Subpaths = new() { [PermissionService.AllSpacesMw] = new() { PermissionService.AllSubpathsMw } },
            ResourceTypes = new() { resourceType },
            Actions = new() { "create" },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = roleName, SpaceName = "management", Subpath = "/roles",
            OwnerShortname = "dmart", IsActive = true,
            Permissions = new() { permName },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return (roleName, permName);
    }

    private static Task UpsertGroupAsync(
        AccessRepository access, string shortname, bool isActive, List<string>? grantableBy)
        => access.UpsertGroupAsync(new Group
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname, SpaceName = "management", Subpath = "/groups",
            OwnerShortname = "dmart", IsActive = isActive,
            GrantableBy = grantableBy,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

    [FactIfPg]
    public async Task NonAdmin_Cannot_Assign_Group_Not_In_Its_GrantableBy()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var (roleName, permName) = await CreateCreatorRoleAsync(access, suffix, "user");
        var targetGroup = "gtarget_" + suffix;
        // grantable_by null ⇒ global-admin-only.
        await UpsertGroupAsync(access, targetGroup, isActive: true, grantableBy: null);
        await access.InvalidateAllCachesAsync();

        var attacker = await _factory.CreateLoggedInUserAsync(roles: new() { roleName });
        var targetUser = "gflrtgt_" + suffix;
        try
        {
            var body = "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + targetUser + "\"," +
                "\"attributes\":{\"groups\":[\"" + targetGroup + "\"],\"is_active\":true}}]}";
            var resp = await attacker.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);

            // Got PAST the CanCreate gate (which would say "not allowed to create
            // user") and was stopped by the floor naming the refused group.
            result!.Status.ShouldBe(Status.Failed);
            raw.ShouldContain("not permitted to assign");
            raw.ShouldContain(targetGroup);
            (await users.GetByShortnameAsync(targetUser)).ShouldBeNull();
        }
        finally
        {
            await attacker.Cleanup();
            try { await users.DeleteAsync(targetUser); } catch { }
            try { await access.DeleteGroupAsync(targetGroup); } catch { }
            try { await access.DeleteRoleAsync(roleName); } catch { }
            try { await access.DeletePermissionAsync(permName); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task NonAdmin_Can_Assign_Group_Delegated_Via_GrantableBy()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var (roleName, permName) = await CreateCreatorRoleAsync(access, suffix, "user");
        var granterGroup = "ggranter_" + suffix;
        var targetGroup = "gtarget_" + suffix;
        await UpsertGroupAsync(access, granterGroup, isActive: true, grantableBy: null);
        // target delegated to anyone in granterGroup.
        await UpsertGroupAsync(access, targetGroup, isActive: true, grantableBy: new() { granterGroup });
        await access.InvalidateAllCachesAsync();

        // attacker holds the create role AND belongs to the granter group.
        var attacker = await _factory.CreateLoggedInUserAsync(
            roles: new() { roleName }, groups: new() { granterGroup });
        var targetUser = "gflrok_" + suffix;
        try
        {
            var body = "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + targetUser + "\"," +
                "\"attributes\":{\"groups\":[\"" + targetGroup + "\"],\"is_active\":true}}]}";
            var resp = await attacker.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Success, raw);
            var created = await users.GetByShortnameAsync(targetUser);
            created.ShouldNotBeNull();
            created!.Groups.ShouldContain(targetGroup);
        }
        finally
        {
            await attacker.Cleanup();
            try { await users.DeleteAsync(targetUser); } catch { }
            try { await access.DeleteGroupAsync(targetGroup); } catch { }
            try { await access.DeleteGroupAsync(granterGroup); } catch { }
            try { await access.DeleteRoleAsync(roleName); } catch { }
            try { await access.DeletePermissionAsync(permName); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task NonAdmin_Cannot_Set_A_Groups_GrantableBy()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        // create grant on GROUP resources (passes CanCreate for a group), not admin.
        var (roleName, permName) = await CreateCreatorRoleAsync(access, suffix, "group");
        await access.InvalidateAllCachesAsync();

        var attacker = await _factory.CreateLoggedInUserAsync(roles: new() { roleName });
        var newGroup = "gnew_" + suffix;
        try
        {
            var body = "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
                "\"resource_type\":\"group\",\"subpath\":\"groups\",\"shortname\":\"" + newGroup + "\"," +
                "\"attributes\":{\"grantable_by\":[\"" + ("g_" + suffix) + "\"],\"is_active\":true}}]}";
            var resp = await attacker.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Failed);
            raw.ShouldContain("assigning grantable_by requires a global admin");
            (await access.GetGroupAsync(newGroup)).ShouldBeNull();
        }
        finally
        {
            await attacker.Cleanup();
            try { await access.DeleteGroupAsync(newGroup); } catch { }
            try { await access.DeleteRoleAsync(roleName); } catch { }
            try { await access.DeletePermissionAsync(permName); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }
}
