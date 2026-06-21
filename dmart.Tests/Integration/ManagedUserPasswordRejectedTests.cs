using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins #96: /managed/request must NOT set user passwords. A create/update that
// carries a non-empty `password` attribute is REJECTED (not silently dropped) so
// the caller isn't misled into thinking it took effect. Passwords flow only
// through /user/create, the OTP password-reset, and self-service /user/profile.
public sealed class ManagedUserPasswordRejectedTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedUserPasswordRejectedTests(DmartFactory factory) => _factory = factory;

    private static string CreateBody(string shortname, bool withPassword) =>
        "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
        "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
        "\"attributes\":{\"is_active\":true,\"email\":\"" + shortname + "@x.y\"" +
        (withPassword ? ",\"password\":\"Sneaky1234\"" : "") + "}}]}";

    [FactIfPg]
    public async Task ManagedRequest_Create_User_With_Password_Is_Rejected()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"itestpw{Guid.NewGuid():N}"[..14];
        try
        {
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(CreateBody(shortname, withPassword: true), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Failed, $"create with a password must be rejected; got: {raw}");
            raw.ShouldContain("password cannot be set");
            (await users.GetByShortnameAsync(shortname))
                .ShouldBeNull("the user must not be persisted when the request is rejected");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_Without_Password_Succeeds_With_No_Password()
    {
        // Control: the same request minus the password is accepted, and the user
        // is persisted passwordless — a password is obtained later via OTP/profile.
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"itestpw{Guid.NewGuid():N}"[..14];
        try
        {
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(CreateBody(shortname, withPassword: false), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Success, $"create without a password must succeed; got: {raw}");
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            created!.Password.ShouldBeNull("managed create must not set a password");
            created.ForcePasswordChange.ShouldBeTrue(
                "managed-created users are always passwordless and must change it at first login");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_ForcePasswordChange_False_Is_Overridden()
    {
        // An explicit `force_password_change: false` in the create request is
        // intentionally ignored — managed-created users are always passwordless
        // and must set a password at first login. The attribute is still
        // honored on the managed UPDATE path, so an admin who needs to clear
        // it can do so immediately after create.
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"itestpw{Guid.NewGuid():N}"[..14];
        try
        {
            var body =
                "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
                "\"attributes\":{\"is_active\":true,\"email\":\"" + shortname + "@x.y\",\"force_password_change\":false}}]}";
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Success, $"create must succeed; got: {raw}");
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            created!.ForcePasswordChange.ShouldBeTrue(
                "managed create must override an explicit force_password_change:false");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Update_User_With_Password_Is_Rejected_And_Hash_Unchanged()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var shortname = $"itestpw{Guid.NewGuid():N}"[..14];
        try
        {
            var originalHash = hasher.Hash("Original1234");
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = "dmart",
                IsActive = true,
                Password = originalHash,
                Language = Language.En,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            var body =
                "{\"space_name\":\"management\",\"request_type\":\"update\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
                "\"attributes\":{\"password\":\"Hijack1234\"}}]}";
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Failed, $"update with a password must be rejected; got: {raw}");
            raw.ShouldContain("password cannot be set");
            (await users.GetByShortnameAsync(shortname))!.Password
                .ShouldBe(originalHash, "a rejected update must not touch the stored password hash");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }
}
