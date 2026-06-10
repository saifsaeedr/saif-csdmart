using System;
using System.Linq;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// AdminBootstrap must provision the `logged_in` role row. PermissionService
// injects that role name implicitly for EVERY authenticated user, but the
// implicit grant resolves to nothing unless an actual management/roles row
// exists — so a fresh deployment should always have one to attach
// permissions to. Bootstrap creates it EMPTY when missing and must never
// touch an existing row: its permissions list belongs to the operator.
public class AdminBootstrapLoggedInRoleTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public AdminBootstrapLoggedInRoleTests(DmartFactory factory) => _factory = factory;

    private (AccessRepository Access, AdminBootstrap Bootstrap) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<AccessRepository>(),
            sp.GetServices<IHostedService>().OfType<AdminBootstrap>().Single());
    }

    [FactIfPg]
    public async Task Bootstrap_Creates_Empty_LoggedIn_Role_When_Missing()
    {
        var (access, bootstrap) = Resolve();
        var snapshot = await access.GetRoleAsync("logged_in");
        try
        {
            await access.DeleteRoleAsync("logged_in");
            await bootstrap.StartAsync(default);

            var role = await access.GetRoleAsync("logged_in");
            role.ShouldNotBeNull("bootstrap must provision the implicit authenticated role");
            role!.Permissions.ShouldBeEmpty(
                "the bootstrapped logged_in role grants nothing until an operator attaches permissions");
            role.SpaceName.ShouldBe("management");
            role.Subpath.ShouldBe("/roles");
            role.IsActive.ShouldBeTrue();
        }
        finally
        {
            if (snapshot is not null) await access.UpsertRoleAsync(snapshot);
        }
    }

    [FactIfPg]
    public async Task Bootstrap_Never_Touches_Existing_LoggedIn_Permissions()
    {
        var (access, bootstrap) = Resolve();
        var snapshot = await access.GetRoleAsync("logged_in");
        try
        {
            var seeded = (snapshot ?? new Role
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = "logged_in",
                SpaceName = "management",
                Subpath = "/roles",
                OwnerShortname = "dmart",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }) with
            {
                Permissions = new() { "operator_attached_perm" },
            };
            await access.UpsertRoleAsync(seeded);

            await bootstrap.StartAsync(default);

            var role = await access.GetRoleAsync("logged_in");
            role.ShouldNotBeNull();
            role!.Permissions.ShouldContain("operator_attached_perm");
        }
        finally
        {
            if (snapshot is not null) await access.UpsertRoleAsync(snapshot);
            else await access.DeleteRoleAsync("logged_in");
        }
    }
}
