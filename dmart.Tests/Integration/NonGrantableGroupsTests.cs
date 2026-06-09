using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Direct coverage of PermissionService.NonGrantableGroupsAsync (the group
// grant-delegation predicate) and the AccessRepository group round-trip, away
// from the HTTP floor path. A group is grantable to a user iff it is active and
// its grantable_by lists a group the actor already belongs to.
public class NonGrantableGroupsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public NonGrantableGroupsTests(DmartFactory factory) => _factory = factory;

    private static Task UpsertGroupAsync(
        AccessRepository access, string shortname, bool isActive, List<string>? grantableBy)
        => access.UpsertGroupAsync(new Group
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname, SpaceName = "management", Subpath = "/groups",
            OwnerShortname = "dmart", IsActive = isActive, GrantableBy = grantableBy,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

    [FactIfPg]
    public async Task Allows_When_Actor_Belongs_To_A_Group_In_GrantableBy()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var perms = _factory.Services.GetRequiredService<PermissionService>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var target = "gng_" + suffix;
        var granter = "gnggr_" + suffix;
        await UpsertGroupAsync(access, target, isActive: true, grantableBy: new() { granter });
        try
        {
            // Actor is a member of `granter`, which is in target.grantable_by ⇒ allowed.
            (await perms.NonGrantableGroupsAsync(new[] { target }, new[] { granter }))
                .ShouldBeEmpty();

            // Actor in some OTHER group ⇒ the target is non-grantable.
            (await perms.NonGrantableGroupsAsync(new[] { target }, new[] { "someoneelse" }))
                .ShouldHaveSingleItem().ShouldBe(target);
        }
        finally { try { await access.DeleteGroupAsync(target); } catch { } }
    }

    [FactIfPg]
    public async Task Disallows_Null_GrantableBy_Inactive_And_Missing()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var perms = _factory.Services.GetRequiredService<PermissionService>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var adminOnly = "gngnull_" + suffix;   // grantable_by null ⇒ global-admin only
        var inactive = "gnginact_" + suffix;   // grantable_by matches but group inactive
        await UpsertGroupAsync(access, adminOnly, isActive: true, grantableBy: null);
        await UpsertGroupAsync(access, inactive, isActive: false, grantableBy: new() { "g" });
        try
        {
            (await perms.NonGrantableGroupsAsync(new[] { adminOnly }, new[] { "g" }))
                .ShouldHaveSingleItem().ShouldBe(adminOnly);

            // Inactive grantee group is non-grantable even though grantable_by matches.
            (await perms.NonGrantableGroupsAsync(new[] { inactive }, new[] { "g" }))
                .ShouldHaveSingleItem().ShouldBe(inactive);

            // A group that does not exist at all is non-grantable.
            var ghost = "ghost_" + suffix;
            (await perms.NonGrantableGroupsAsync(new[] { ghost }, new[] { "g" }))
                .ShouldHaveSingleItem().ShouldBe(ghost);

            // Empty request ⇒ nothing disallowed (no DB round-trip).
            (await perms.NonGrantableGroupsAsync(Array.Empty<string>(), new[] { "g" }))
                .ShouldBeEmpty();
        }
        finally
        {
            try { await access.DeleteGroupAsync(adminOnly); } catch { }
            try { await access.DeleteGroupAsync(inactive); } catch { }
        }
    }

    [FactIfPg]
    public async Task Group_Repository_RoundTrip_Upsert_Get_GetMany_Delete()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var name = "grt_" + suffix;
        await access.UpsertGroupAsync(new Group
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = name, SpaceName = "management", Subpath = "/groups",
            OwnerShortname = "dmart", IsActive = true,
            Tags = new() { "t1", "t2" },
            GrantableBy = new() { "g1" },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        try
        {
            var got = await access.GetGroupAsync(name);
            got.ShouldNotBeNull();
            got!.Shortname.ShouldBe(name);
            got.IsActive.ShouldBeTrue();
            got.Tags.ShouldContain("t1");
            got.GrantableBy.ShouldNotBeNull();
            got.GrantableBy!.ShouldContain("g1");

            (await access.GetGroupsAsync(new[] { name })).Select(g => g.Shortname).ShouldContain(name);

            (await access.DeleteGroupAsync(name)).ShouldBeTrue();
            (await access.GetGroupAsync(name)).ShouldBeNull();
        }
        finally { try { await access.DeleteGroupAsync(name); } catch { } }
    }
}
