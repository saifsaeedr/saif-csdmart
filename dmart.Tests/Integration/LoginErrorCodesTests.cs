using System.Net;
using System.Net.Http.Json;
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

// Asserts Python-exact (type, code, message) triples on /user/login errors.
// Every test provisions its own user so cross-class parallelism can't taint
// shared admin state.
public sealed class LoginErrorCodesTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public LoginErrorCodesTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task UserNotFound_Returns_USERNAME_NOT_EXIST()
    {
        if (!DmartFactory.HasPg) return;
        var (type, code, msg) = await ExpectLoginFailureAsync(
            new UserLoginRequest($"ghost_{Guid.NewGuid():N}"[..16], null, null, "pw", null));
        type.ShouldBe("auth");
        code.ShouldBe(InternalErrorCode.USERNAME_NOT_EXIST);
        msg.ShouldBe("Invalid username or password");
    }

    [Fact]
    public async Task WrongPassword_Returns_PASSWORD_NOT_VALIDATED()
    {
        if (!DmartFactory.HasPg) return;
        var (shortname, _) = await CreateUserAsync(password: "correct-pw");
        try
        {
            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, "wrong-pw", null));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.PASSWORD_NOT_VALIDATED);
            msg.ShouldBe("Invalid username or password");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [Fact]
    public async Task Mobile_NewDevice_Returns_OTP_NEEDED()
    {
        if (!DmartFactory.HasPg) return;

        // Mobile user with an existing device_id triggers the OTP path on a mismatch.
        var (shortname, pw) = await CreateUserAsync(
            password: "correct-pw",
            type: UserType.Mobile,
            deviceId: "old-dev");
        try
        {
            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, pw, null,
                    Otp: null, DeviceId: "new-dev"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.OTP_NEEDED);
            msg.ShouldBe("New device detected, login with otp");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [Fact]
    public async Task LockedToDevice_Returns_USER_ACCOUNT_LOCKED()
    {
        if (!DmartFactory.HasPg) return;
        var (shortname, pw) = await CreateUserAsync(
            password: "correct-pw",
            type: UserType.Web,
            deviceId: "bound-dev",
            lockedToDevice: true);
        try
        {
            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, pw, null,
                    Otp: null, DeviceId: "stranger-dev"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            msg.ShouldBe("This account is locked to a unique device !");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [Fact]
    public async Task OtpLogin_WrongCode_Returns_OTP_INVALID()
    {
        if (!DmartFactory.HasPg) return;
        var (shortname, _) = await CreateUserAsync(password: null);
        try
        {
            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, null, null, Otp: "000000"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.OTP_INVALID);
            msg.ShouldBe("Wrong OTP");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [Fact]
    public async Task OtpLogin_MultipleIdentifiers_Returns_OTP_ISSUE()
    {
        if (!DmartFactory.HasPg) return;
        var (type, code, msg) = await ExpectLoginFailureAsync(
            new UserLoginRequest("u", "u@x.com", null, null, null, Otp: "123456"));
        type.ShouldBe("auth");
        code.ShouldBe(InternalErrorCode.OTP_ISSUE);
        msg.ShouldBe("Provide either msisdn, email or shortname, not both.");
    }

    [Fact]
    public async Task OtpLogin_NoIdentifier_Returns_OTP_ISSUE()
    {
        if (!DmartFactory.HasPg) return;
        var (type, code, msg) = await ExpectLoginFailureAsync(
            new UserLoginRequest(null, null, null, null, null, Otp: "123456"));
        type.ShouldBe("auth");
        code.ShouldBe(InternalErrorCode.OTP_ISSUE);
        msg.ShouldBe("Either msisdn, email or shortname must be provided.");
    }

    [Fact]
    public async Task Invitation_UnknownToken_Returns_INVALID_INVITATION_jwtauth()
    {
        if (!DmartFactory.HasPg) return;

        // A JWT with a valid signature but no DB row — the repo lookup fails,
        // so invitation login rejects with type=jwtauth code=INVALID_INVITATION.
        var (shortname, _) = await CreateUserAsync(password: null);
        try
        {
            var jwt = _factory.Services.GetRequiredService<InvitationJwt>();
            var token = jwt.Mint(shortname, InvitationChannel.Email);

            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(null, null, null, null, token));
            type.ShouldBe("jwtauth");
            code.ShouldBe(InternalErrorCode.INVALID_INVITATION);
            msg.ShouldBe("Expired or invalid invitation");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // ---- helpers ----

    private async Task<(string Type, int Code, string Message)> ExpectLoginFailureAsync(UserLoginRequest login)
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error.ShouldNotBeNull();
        return (body.Error!.Type, body.Error.Code, body.Error.Message);
    }

    private async Task<(string Shortname, string Password)> CreateUserAsync(
        string? password = "Test1234!abc",
        UserType type = UserType.Web,
        string? deviceId = null,
        bool lockedToDevice = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"login_err_{suffix}";
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = password is null ? null : hasher.Hash(password),
            Type = type,
            Language = Language.En,
            DeviceId = deviceId,
            LockedToDevice = lockedToDevice,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return (shortname, password ?? "");
    }

    private async Task DeleteUserAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAllSessionsAsync(shortname);
            await users.DeleteAsync(shortname);
        }
        catch { /* best effort */ }
    }
}
