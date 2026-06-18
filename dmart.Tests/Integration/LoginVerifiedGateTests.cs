using System.Net;
using System.Net.Http.Json;
using System.Text;
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

// Channel-specific verification gate on /user/login, enforced on BOTH the
// password and OTP paths:
//   * login via email  → requires is_email_verified
//   * login via msisdn → requires is_msisdn_verified
//   * login via shortname → no verification requirement
// The gate runs only after the credential check succeeds, so an unauthenticated
// caller can't use it as a verification oracle.
public sealed class LoginVerifiedGateTests : IClassFixture<DmartFactory>
{
    private const string Password = "Test1234aaa";
    private readonly DmartFactory _factory;
    public LoginVerifiedGateTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task EmailLogin_Blocked_When_Email_Not_Verified()
    {
        var (shortname, email, _) = await SeedUserAsync(emailVerified: false);
        try
        {
            var resp = await Login(new UserLoginRequest(null, email, null, Password, null));
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.USER_ISNT_VERIFIED);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task EmailLogin_Allowed_When_Email_Verified()
    {
        var (shortname, email, _) = await SeedUserAsync(emailVerified: true);
        try
        {
            var resp = await Login(new UserLoginRequest(null, email, null, Password, null));
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task MsisdnOtpLogin_Blocked_When_Msisdn_Not_Verified()
    {
        var (shortname, _, msisdn) = await SeedUserAsync(
            emailVerified: false, msisdnVerified: false, withMsisdn: true);
        try
        {
            // Seed a valid login OTP at the bare msisdn key (the dest the
            // msisdn-identifier OTP path resolves to).
            var otpRepo = _factory.Services.GetRequiredService<OtpRepository>();
            const string code = "123456";
            await otpRepo.StoreAsync(msisdn!, code, DateTime.UtcNow.AddMinutes(5));

            var resp = await Login(new UserLoginRequest(null, null, msisdn, null, Otp: code));
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.USER_ISNT_VERIFIED);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task ShortnameLogin_Allowed_When_Contacts_Unverified()
    {
        // Both contacts unverified, but login is by shortname → no gate.
        var (shortname, _, _) = await SeedUserAsync(emailVerified: false, msisdnVerified: false, withMsisdn: true);
        try
        {
            var resp = await Login(new UserLoginRequest(shortname, null, null, Password, null));
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task ShortnameLogin_Ignores_Echoed_Unverified_Email()
    {
        // Body carries BOTH a shortname and an (unverified) email. The user is
        // resolved by shortname (precedence), so this is a shortname login and
        // the email-verification gate must not fire.
        var (shortname, email, _) = await SeedUserAsync(emailVerified: false);
        try
        {
            var resp = await Login(new UserLoginRequest(shortname, email, null, Password, null));
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task AdminCreated_User_Defaults_Unverified_And_Is_Blocked_On_Email_Login()
    {
        // The realistic installed-base trigger the seed-based tests above don't
        // cover: an admin creating a user via /managed/request does NOT pass
        // is_email_verified, so RequestHandler defaults it to false. Such a user
        // must then be blocked on email login by the verification gate — this is
        // the behavior change that can lock out existing admin-provisioned users,
        // pinned here end-to-end through the real create path.
        var admin = await _factory.CreateLoggedInUserAsync();
        string? shortname = null;
        try
        {
            var created = await CreateUserViaManagedAsync(admin.Client, emailVerified: false);
            shortname = created.Shortname;

            // Document the default: the managed create path leaves email unverified.
            var users = _factory.Services.GetRequiredService<UserRepository>();
            (await users.GetByShortnameAsync(shortname))!.IsEmailVerified.ShouldBeFalse(
                "managed/admin create must default is_email_verified to false");

            // Correct password → credential check passes → the gate (which runs
            // only after credentials) is what rejects the login.
            var resp = await Login(new UserLoginRequest(null, created.Email, null, Password, null));
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.USER_ISNT_VERIFIED);
        }
        finally
        {
            if (shortname is not null) await DeleteUserAsync(shortname);
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task AdminCreated_User_With_IsEmailVerified_Can_Email_Login()
    {
        // Positive control through the same managed path: when the admin sets
        // is_email_verified=true, email login succeeds. Proves it's the flag —
        // not the managed-create path itself — that gates the negative case.
        var admin = await _factory.CreateLoggedInUserAsync();
        string? shortname = null;
        try
        {
            var created = await CreateUserViaManagedAsync(admin.Client, emailVerified: true);
            shortname = created.Shortname;

            var resp = await Login(new UserLoginRequest(null, created.Email, null, Password, null));
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);
        }
        finally
        {
            if (shortname is not null) await DeleteUserAsync(shortname);
            await admin.Cleanup();
        }
    }

    // ---- helpers ----

    // Creates a Web user through the real admin path (POST /managed/request,
    // resource_type=user) using an authenticated super-admin client. Mirrors the
    // payload shape ManagedRequestCreateResponseTests drives. is_email_verified
    // is only sent when emailVerified is true, so the false case exercises the
    // RequestHandler default (the installed-base scenario under test).
    private async Task<(string Shortname, string Email)> CreateUserViaManagedAsync(
        HttpClient admin, bool emailVerified)
    {
        var shortname = $"agate_{Guid.NewGuid():N}"[..16];
        var email = $"{shortname}@x.y";
        var verifiedAttr = emailVerified ? ",\"is_email_verified\":true" : "";
        var bodyJson = "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/users\"," +
            "\"attributes\":{\"email\":\"" + email + "\",\"password\":\"" + Password + "\""
            + verifiedAttr + "}}]}";
        var resp = await admin.PostAsync("/managed/request",
            new StringContent(bodyJson, Encoding.UTF8, "application/json"));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
            .Status.ShouldBe(Status.Success);
        return (shortname, email);
    }

    private Task<HttpResponseMessage> Login(UserLoginRequest req) =>
        _factory.CreateClient().PostAsJsonAsync("/user/login", req, DmartJsonContext.Default.UserLoginRequest);

    private async Task<(string Shortname, string Email, string? Msisdn)> SeedUserAsync(
        bool emailVerified, bool msisdnVerified = false, bool withMsisdn = false)
    {
        var shortname = $"vgate_{Guid.NewGuid():N}"[..16];
        var email = $"{shortname}@x.y";
        var msisdn = withMsisdn ? $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}" : null;
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(Password),
            Email = email,
            IsEmailVerified = emailVerified,
            Msisdn = msisdn,
            IsMsisdnVerified = msisdnVerified,
            Type = UserType.Web,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return (shortname, email, msisdn);
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
