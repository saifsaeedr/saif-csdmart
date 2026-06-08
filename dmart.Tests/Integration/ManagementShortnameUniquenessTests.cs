using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Roles, groups, and permissions are fetched and deleted by shortname ALONE, so
// the schema enforces a globally-unique shortname per table via a unique index —
// stronger than the composite UNIQUE(shortname,space,subpath). Each test inserts a
// second row with the same shortname under a DIFFERENT subpath (which slips past
// the composite ON CONFLICT key) and asserts the unique index rejects it (SQLSTATE
// 23505).
public class ManagementShortnameUniquenessTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagementShortnameUniquenessTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Role_Shortname_Is_Globally_Unique_Across_Subpaths()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var name = "runiq_" + Guid.NewGuid().ToString("N")[..6];
        Role Make(string subpath) => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = name, SpaceName = "management", Subpath = subpath,
            OwnerShortname = "dmart", IsActive = true, Permissions = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await access.UpsertRoleAsync(Make("/roles"));
        try
        {
            var ex = await Should.ThrowAsync<Npgsql.PostgresException>(
                () => access.UpsertRoleAsync(Make("/roles/other")));
            ex.SqlState.ShouldBe("23505"); // unique_violation
        }
        finally { try { await access.DeleteRoleAsync(name); } catch { } }
    }

    [FactIfPg]
    public async Task Group_Shortname_Is_Globally_Unique_Across_Subpaths()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var name = "guniq_" + Guid.NewGuid().ToString("N")[..6];
        Group Make(string subpath) => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = name, SpaceName = "management", Subpath = subpath,
            OwnerShortname = "dmart", IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await access.UpsertGroupAsync(Make("/groups"));
        try
        {
            var ex = await Should.ThrowAsync<Npgsql.PostgresException>(
                () => access.UpsertGroupAsync(Make("/groups/other")));
            ex.SqlState.ShouldBe("23505");
        }
        finally { try { await access.DeleteGroupAsync(name); } catch { } }
    }

    [FactIfPg]
    public async Task Permission_Shortname_Is_Globally_Unique_Across_Subpaths()
    {
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var name = "puniq_" + Guid.NewGuid().ToString("N")[..6];
        Permission Make(string subpath) => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = name, SpaceName = "management", Subpath = subpath,
            OwnerShortname = "dmart", IsActive = true,
            Subpaths = new() { ["management"] = new() { "/" } },
            ResourceTypes = new() { "content" },
            Actions = new() { "view" },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await access.UpsertPermissionAsync(Make("/permissions"));
        try
        {
            var ex = await Should.ThrowAsync<Npgsql.PostgresException>(
                () => access.UpsertPermissionAsync(Make("/permissions/other")));
            ex.SqlState.ShouldBe("23505");
        }
        finally { try { await access.DeletePermissionAsync(name); } catch { } }
    }
}
