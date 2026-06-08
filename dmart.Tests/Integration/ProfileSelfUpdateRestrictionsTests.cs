using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Self-service /user/profile must not let a user change privileged fields on
// themselves: force_password_change is ignored (Python comments out the patch-set,
// router.py:683-684), and any configured user_profile_payload_protected_fields in
// payload.body are rejected — in BOTH the JsonElement and Dictionary payload shapes.
public class ProfileSelfUpdateRestrictionsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ProfileSelfUpdateRestrictionsTests(DmartFactory factory) => _factory = factory;

    private static async Task<string> CreateUserAsync(WebApplicationFactory<Program> factory)
    {
        var users = factory.Services.GetRequiredService<UserRepository>();
        var hasher = factory.Services.GetRequiredService<PasswordHasher>();
        var shortname = "prest_" + Guid.NewGuid().ToString("N")[..10];
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname, SpaceName = "management", Subpath = "/users",
            OwnerShortname = shortname, IsActive = true,
            Password = hasher.Hash("OldPass1234!"),
            ForcePasswordChange = false,
            Type = UserType.Web, Language = Language.En,
            Roles = new(), Groups = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return shortname;
    }

    [FactIfPg]
    public async Task ForcePasswordChange_Is_Not_Self_Settable()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var svc = _factory.Services.GetRequiredService<UserService>();
        var shortname = await CreateUserAsync(_factory);
        try
        {
            // Patch tries to flip it on, with no password change in the patch.
            var result = await svc.UpdateProfileAsync(shortname,
                new Dictionary<string, object> { ["force_password_change"] = true });
            result.IsOk.ShouldBeTrue(result.ErrorMessage);
            (await users.GetByShortnameAsync(shortname))!.ForcePasswordChange
                .ShouldBeFalse("a user must not set force_password_change on themselves");
        }
        finally { try { await users.DeleteAsync(shortname); } catch { } }
    }

    [FactIfPg]
    public async Task Protected_Payload_Field_Is_Rejected_JsonElement_Form()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s => s.UserProfilePayloadProtectedFields = "secret")));
        var users = factory.Services.GetRequiredService<UserRepository>();
        var svc = factory.Services.GetRequiredService<UserService>();
        var shortname = await CreateUserAsync(factory);
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>("{\"body\":{\"secret\":\"x\"}}");
            var result = await svc.UpdateProfileAsync(shortname,
                new Dictionary<string, object> { ["payload"] = payload });
            result.IsOk.ShouldBeFalse();
            result.ErrorCode.ShouldBe(InternalErrorCode.PROTECTED_FIELD);
        }
        finally { try { await users.DeleteAsync(shortname); } catch { } }
    }

    [FactIfPg]
    public async Task Protected_Payload_Field_Is_Rejected_Dictionary_Form()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s => s.UserProfilePayloadProtectedFields = "secret")));
        var users = factory.Services.GetRequiredService<UserRepository>();
        var svc = factory.Services.GetRequiredService<UserService>();
        var shortname = await CreateUserAsync(factory);
        try
        {
            // payload as Dictionary{ body = JsonElement } — the shape the old
            // JsonElement-only check missed but the merge would still apply.
            var patch = new Dictionary<string, object>
            {
                ["payload"] = new Dictionary<string, object>
                {
                    ["body"] = JsonSerializer.Deserialize<JsonElement>("{\"secret\":\"x\"}"),
                },
            };
            var result = await svc.UpdateProfileAsync(shortname, patch);
            result.IsOk.ShouldBeFalse();
            result.ErrorCode.ShouldBe(InternalErrorCode.PROTECTED_FIELD);
        }
        finally { try { await users.DeleteAsync(shortname); } catch { } }
    }
}
