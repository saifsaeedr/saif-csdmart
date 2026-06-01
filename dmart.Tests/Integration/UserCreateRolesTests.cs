using System.Net.Http.Json;
using System.Text;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the self-service registration access policy for POST /user/create:
//   * a caller can never grant themselves roles/groups — any roles/groups in
//     the request body are ignored (the privilege-escalation guard);
//   * when user_create_default_role / user_create_default_group are set, every
//     self-created user receives exactly that single role/group instead.
// The managed/admin create path (/managed/request) is a separate code path and
// still honors caller-supplied roles/groups for authorized admins — that path
// is not exercised here.
public class UserCreateRolesTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserCreateRolesTests(DmartFactory factory) => _factory = factory;

    // Body that asks for escalated access. /user/create allocates the
    // shortname server-side, so none is sent on the wire.
    private static StringContent EscalatedCreateBody(string email) => new(
        "{\"attributes\":{\"email\":\"" + email + "\",\"password\":\"Testtest1234\","
        + "\"roles\":[\"super_admin\"],\"groups\":[\"admins\"]}}",
        Encoding.UTF8, "application/json");

    [FactIfPg]
    public async Task Create_Ignores_Client_Supplied_Roles_And_Groups_When_No_Default()
    {
        // No default role/group configured → the new user must end up with
        // empty access regardless of what the body asked for. OTP disabled so
        // the create succeeds without a prior /user/otp-request.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s =>
            {
                s.IsOtpForCreateRequired = false;
                s.UserCreateDefaultRole = null;
                s.UserCreateDefaultGroup = null;
            })));
        var client = factory.CreateClient();

        var email = "rolesfree_" + Guid.NewGuid().ToString("N")[..6] + "@x.y";
        var resp = await client.PostAsync("/user/create", EscalatedCreateBody(email));
        resp.IsSuccessStatusCode.ShouldBeTrue();
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var shortname = result!.Records![0].Shortname;

        var users = factory.Services.GetRequiredService<UserRepository>();
        var created = await users.GetByShortnameAsync(shortname);
        created.ShouldNotBeNull();
        created!.Roles.ShouldBeEmpty();
        created.Groups.ShouldBeEmpty();

        await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
    }

    [FactIfPg]
    public async Task Create_Applies_Configured_Defaults_Over_Client_Supplied_Values()
    {
        // Random names so the assertion can't accidentally match a role/group
        // the caller put in the body.
        var defaultRole = "defrole_" + Guid.NewGuid().ToString("N")[..6];
        var defaultGroup = "defgroup_" + Guid.NewGuid().ToString("N")[..6];
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s =>
            {
                s.IsOtpForCreateRequired = false;
                s.UserCreateDefaultRole = defaultRole;
                s.UserCreateDefaultGroup = defaultGroup;
            })));
        var client = factory.CreateClient();

        var email = "defaultacc_" + Guid.NewGuid().ToString("N")[..6] + "@x.y";
        var resp = await client.PostAsync("/user/create", EscalatedCreateBody(email));
        resp.IsSuccessStatusCode.ShouldBeTrue();
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var shortname = result!.Records![0].Shortname;

        var users = factory.Services.GetRequiredService<UserRepository>();
        var created = await users.GetByShortnameAsync(shortname);
        created.ShouldNotBeNull();
        // The body's super_admin / admins were discarded; only the configured
        // single default role and group survive.
        created!.Roles.ShouldBe(new[] { defaultRole });
        created.Groups.ShouldBe(new[] { defaultGroup });

        await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
    }
}
