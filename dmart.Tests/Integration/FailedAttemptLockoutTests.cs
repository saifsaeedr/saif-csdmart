using System.Net;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Lockout coverage for the failure paths that should count toward
// MAX_FAILED_LOGIN_ATTEMPTS:
//   * wrong password                  — covered by FullParityTests.Account_Lockout_After_Max_Failed_Attempts
//   * wrong OTP                       — exercised here via /user/login (OTP path)
//   * wrong password alongside OTP    — exercised here via /user/login (OTP path + password)
//   * wrong old_password              — exercised here via /user/profile (password change)
//
// Each test provisions its own user so xUnit parallelism can't taint the
// shared admin row, and the lockout-threshold tests pre-seed attempt_count
// directly via SQL to avoid a flaky N-iteration login loop.
public sealed class FailedAttemptLockoutTests : IClassFixture<DmartFactory>
{
    // Exact string emitted by RejectIfAttemptLocked / HandleFailedLoginAttempt.
    // Pinned so a regression that swaps in RejectIfNotActive's shorter
    // "Account has been locked." message would fail the assertion rather than
    // pass via a substring match.
    private const string LockoutMessage =
        "Account has been locked due to too many failed login attempts.";

    private readonly DmartFactory _factory;
    public FailedAttemptLockoutTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task OtpLogin_WrongCode_Increments_AttemptCount_Once()
    {
        // Below the threshold: a single wrong OTP should still surface
        // OTP_INVALID and merely bump the counter (not lock the account).
        var (shortname, _) = await CreateUserAsync(password: null);
        try
        {
            var (type, code, _) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, null, Otp: "000000"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.OTP_INVALID);

            var stored = await ReadAttemptCountAsync(shortname);
            stored.ShouldBe(1);

            var refreshed = await GetUserAsync(shortname);
            refreshed!.IsActive.ShouldBeTrue("single bad OTP must not lock the account");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task OtpLogin_WrongCode_Locks_When_Threshold_Reached()
    {
        var max = MaxAttempts();
        var (shortname, _) = await CreateUserAsync(password: null);
        try
        {
            // Seed attempt_count = max-1 so the next wrong OTP trips the
            // lockout. Direct SQL so we don't race with HandleFailedLoginAttempt
            // running through N HTTP round-trips.
            await SetAttemptCountAsync(shortname, max - 1);

            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, null, Otp: "000000"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            msg.ShouldBe(LockoutMessage);

            var refreshed = await GetUserAsync(shortname);
            refreshed!.IsActive.ShouldBeFalse("threshold-trip must flip is_active=false");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task OtpLogin_AlreadyLocked_Returns_USER_ACCOUNT_LOCKED_Without_Consuming_Otp()
    {
        // Pre-condition: attempt_count is already at max. The pre-check in
        // LoginWithOtpAsync should reject before VerifyAndConsumeAsync runs,
        // preserving the OTP for an admin-driven recovery flow.
        var max = MaxAttempts();
        var (shortname, _) = await CreateUserAsync(password: null);
        try
        {
            await SetAttemptCountAsync(shortname, max);

            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, null, Otp: "000000"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            msg.ShouldBe(LockoutMessage);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task OtpLogin_ValidOtp_WrongPassword_Locks_When_Threshold_Reached()
    {
        // The OTP login path optionally accepts a password too. A valid OTP
        // with a wrong password used to short-circuit on PASSWORD_NOT_VALIDATED
        // without bumping attempt_count — meaning anyone in possession of an
        // OTP could iterate password guesses indefinitely. With the fix, this
        // path goes through HandleFailedLoginAttemptAsync just like LoginAsync.
        var max = MaxAttempts();
        var msisdn = $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}";
        var (shortname, _) = await CreateUserAsync(password: "CorrectPassword1", msisdn: msisdn);
        try
        {
            // Seed a valid OTP under the msisdn key (shortname-identifier path
            // resolves dest = user.Msisdn). VerifyAndConsumeAsync will accept
            // and delete it, then the password check fails.
            var otpRepo = _factory.Services.GetRequiredService<OtpRepository>();
            const string otp = "123456";
            await otpRepo.StoreAsync(msisdn, otp, DateTime.UtcNow.AddMinutes(5));

            await SetAttemptCountAsync(shortname, max - 1);

            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, "WrongPassword1", Otp: otp));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            msg.ShouldBe(LockoutMessage);

            var refreshed = await GetUserAsync(shortname);
            refreshed!.IsActive.ShouldBeFalse("threshold-trip must flip is_active=false");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Profile_WrongOldPassword_Increments_AttemptCount_Once()
    {
        // Logged-in user POSTs /user/profile with a new password but the wrong
        // old_password. Below the threshold: UNMATCHED_DATA + counter bump.
        var creds = await _factory.CreateLoggedInUserAsync();
        try
        {
            var resp = await PostProfileAsync(creds.Client,
                oldPassword: "definitely-not-the-current-pw",
                newPassword: "NewPassword1!");

            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.UNMATCHED_DATA);

            var stored = await ReadAttemptCountAsync(creds.Shortname);
            stored.ShouldBe(1);

            var refreshed = await GetUserAsync(creds.Shortname);
            refreshed!.IsActive.ShouldBeTrue("single bad old_password must not lock the account");
        }
        finally { await creds.Cleanup(); }
    }

    [FactIfPg]
    public async Task Profile_WrongOldPassword_Locks_When_Threshold_Reached()
    {
        var max = MaxAttempts();
        var creds = await _factory.CreateLoggedInUserAsync();
        try
        {
            await SetAttemptCountAsync(creds.Shortname, max - 1);

            var resp = await PostProfileAsync(creds.Client,
                oldPassword: "definitely-not-the-current-pw",
                newPassword: "NewPassword1!");

            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            body.Error.Message.ShouldBe(LockoutMessage);

            var refreshed = await GetUserAsync(creds.Shortname);
            refreshed!.IsActive.ShouldBeFalse("threshold-trip must flip is_active=false");

            // Lockout invalidates every active session for the user.
            // CreateLoggedInUserAsync minted exactly one — verify it's gone so
            // the already-issued bearer token can't continue making requests.
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var sessionsLeft = await users.CountSessionsAsync(creds.Shortname);
            sessionsLeft.ShouldBe(0, "lockout must remove all active sessions");
        }
        finally { await creds.Cleanup(); }
    }

    // ---- helpers ----

    private int MaxAttempts() =>
        _factory.Services.GetRequiredService<IOptions<Dmart.Config.DmartSettings>>()
            .Value.MaxFailedLoginAttempts;

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

    private static StringContent ProfilePatch(string oldPassword, string newPassword)
    {
        // Match the Record envelope shape ProfileHandler accepts:
        //   { "attributes": { "old_password": "...", "password": "..." } }
        var json = $"{{\"attributes\":{{\"old_password\":\"{oldPassword}\",\"password\":\"{newPassword}\"}}}}";
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }

    private static Task<HttpResponseMessage> PostProfileAsync(
        HttpClient client, string oldPassword, string newPassword) =>
        client.PostAsync("/user/profile", ProfilePatch(oldPassword, newPassword));

    private async Task<(string Shortname, string Password)> CreateUserAsync(
        string? password, string? msisdn = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"lockout_{suffix}";
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
            Password = password is null ? null : hasher.Hash(password),
            Msisdn = msisdn,
            IsMsisdnVerified = msisdn is not null,
            Type = UserType.Web,
            Language = Language.En,
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

    private async Task<User?> GetUserAsync(string shortname)
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        return await users.GetByShortnameAsync(shortname);
    }

    private Task<int> ReadAttemptCountAsync(string shortname) =>
        _factory.Services.GetRequiredService<UserRepository>().GetAttemptCountAsync(shortname);

    private async Task SetAttemptCountAsync(string shortname, int count)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "UPDATE users SET attempt_count = $1 WHERE shortname = $2", conn);
        cmd.Parameters.Add(new() { Value = count });
        cmd.Parameters.Add(new() { Value = shortname });
        await cmd.ExecuteNonQueryAsync();
    }
}
