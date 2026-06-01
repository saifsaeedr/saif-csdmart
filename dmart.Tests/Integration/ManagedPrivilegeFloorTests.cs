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

// Security: even with a (broad) create grant, a NON-global-admin must not be
// able to mint a super_admin user via /managed/request. The privilege floor
// rejects assigning a role the actor doesn't hold, independent of whether the
// deployment configured restricted_fields on the delegated permission.
public class ManagedPrivilegeFloorTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedPrivilegeFloorTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task NonAdmin_With_Create_Grant_Cannot_Assign_SuperAdmin()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var permName = "floorperm_" + suffix;
        var roleName = "floorrole_" + suffix;

        // create-only over ALL spaces: enough to pass the CanCreate gate for a
        // user resource, but NOT a global admin (that requires create AND
        // update via __all_spaces__/__all_subpaths__), so the floor applies.
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = permName, SpaceName = "management", Subpath = "/permissions",
            OwnerShortname = "dmart", IsActive = true,
            Subpaths = new() { [PermissionService.AllSpacesMw] = new() { PermissionService.AllSubpathsMw } },
            ResourceTypes = new() { "user" },
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
        await access.InvalidateAllCachesAsync();

        var attacker = await _factory.CreateLoggedInUserAsync(roles: new() { roleName });
        try
        {
            var body = "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"floortgt_" + suffix + "\"," +
                "\"attributes\":{\"roles\":[\"super_admin\"],\"is_active\":true}}]}";
            var resp = await attacker.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);

            // /managed/request reports a per-record failure as an aggregate
            // SOMETHING_WRONG envelope, with the actual error in `info.failed`.
            result!.Status.ShouldBe(Status.Failed);
            // The floor's message names the refused role — proving we got PAST
            // the CanCreate gate (which would say "not allowed to create user")
            // and were stopped by the privilege floor.
            raw.ShouldContain("do not hold");
            raw.ShouldContain("super_admin");

            // And no escalated user was actually persisted.
            (await users.GetByShortnameAsync("floortgt_" + suffix)).ShouldBeNull();
        }
        finally
        {
            await attacker.Cleanup();
            try { await users.DeleteAsync("floortgt_" + suffix); } catch { }
            try { await access.DeleteRoleAsync(roleName); } catch { }
            try { await access.DeletePermissionAsync(permName); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }
}
