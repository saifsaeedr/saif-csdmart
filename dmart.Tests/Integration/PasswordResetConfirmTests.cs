using System.Net;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// /user/password-reset-confirm completes the reset flow: it verifies the OTP
// minted by /user/password-reset-request (keyed under "pwd-reset:{dest}") and
// writes a new password hash. Two security guarantees pinned here:
//   1. Key prefix isolation — a reset OTP must NOT be consumable via
//      /user/login's OTP path, which reads the bare {dest} key.
//   2. Brute-force lockout — wrong OTPs count against the failed-attempt
//      counter, so a distributed attacker can't exhaust the 10^6 6-digit
//      keyspace within the 300s TTL.
// Identifier on the confirm side is the same typed-field shape as
// /password-reset-request: exactly one of {Shortname, Email, Msisdn}.
public sealed class PasswordResetConfirmTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PasswordResetConfirmTests(DmartFactory factory) => _factory = factory;

    private const string ValidPassword = "NewPass1234";

    // Mirrors OtpHandler.ResetOtpPrefix — duplicated here because the handler
    // constant is private. Test cleanup needs the literal so any divergence
    // shows up as a failing test rather than a silently orphaned row.
    private const string ResetPrefix = "pwd-reset:";

    [FactIfPg]
    public async Task HappyPath_Request_Then_Confirm_Updates_Password()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();

            // 1. Mint a reset OTP for the user.
            var reqResp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            reqResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Recover the OTP value the handler stored under pwd-reset:{msisdn}.
            var code = await ReadOtpCodeAsync(msisdn);
            code.ShouldNotBeNullOrEmpty();

            // 2. Confirm with the correct OTP + a valid new password.
            var confirmResp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: code!, Password: ValidPassword),
                DmartJsonContext.Default.PasswordResetConfirm);
            confirmResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The user row's password hash must verify against the new password.
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
            var updated = await users.GetByShortnameAsync(shortname);
            updated.ShouldNotBeNull();
            hasher.Verify(ValidPassword, updated!.Password!).ShouldBeTrue();

            // OTP row must be consumed (atomic verify+delete in the repo).
            (await ReadOtpCodeAsync(msisdn)).ShouldBeNull();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task Reset_Otp_Is_Not_Consumable_Via_Login_OtpPath()
    {
        // Cross-flow isolation: the reset OTP lives at pwd-reset:{dest};
        // /user/login's OTP path reads bare {dest}, so the reset code must
        // not authenticate a login.
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var reqResp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            reqResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var code = await ReadOtpCodeAsync(msisdn);
            code.ShouldNotBeNullOrEmpty();

            // Attempt to log in with the reset code via the OTP path. Login
            // calls VerifyAndConsumeAsync(bareMsisdn, code) — that key has no
            // row, so login must fail.
            var loginResp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(null, null, msisdn, null, null, code),
                DmartJsonContext.Default.UserLoginRequest);
            loginResp.IsSuccessStatusCode.ShouldBeFalse(
                "reset OTP at pwd-reset:{msisdn} must not authenticate a login");

            // The reset OTP must still be present (login's failed VerifyAndConsume
            // ran on the unrelated bare-msisdn key and didn't touch the reset row).
            (await ReadOtpCodeAsync(msisdn)).ShouldNotBeNullOrEmpty();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task SecondRequest_Within_Cooldown_Is_SilentlyOk()
    {
        // Anti-enumeration: the cooldown branch returns 200 Ok silently so a
        // paired-request attacker can't distinguish "known user, just-issued
        // OTP" (cooldown hit) from "unknown user" (early return).
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var first = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            var firstCode = await ReadOtpCodeAsync(msisdn);
            firstCode.ShouldNotBeNullOrEmpty();

            // Second call within the default 60s cooldown — must return 200
            // Ok AND must NOT refresh the stored code (otherwise the cooldown
            // is observable via the OTP value changing).
            var second = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            second.StatusCode.ShouldBe(HttpStatusCode.OK);

            var secondCode = await ReadOtpCodeAsync(msisdn);
            secondCode.ShouldBe(firstCode, "cooldown must be a true no-op — same OTP code persists");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task WrongOtp_Returns_OtpInvalid()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);

            // Submit a definitely-wrong 6-digit code.
            var resp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: "000000", Password: ValidPassword),
                DmartJsonContext.Default.PasswordResetConfirm);
            // OTP_INVALID → HTTP 400 (FailedResponseFilter default for OTP codes).
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            body.ShouldContain("code mismatch or expired");

            // The reset OTP must remain available (VerifyAndConsumeAsync only
            // deletes on success).
            (await ReadOtpCodeAsync(msisdn)).ShouldNotBeNullOrEmpty();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task UnknownIdentifier_Returns_OtpInvalid()
    {
        var client = _factory.CreateClient();
        var unknown = $"ghost_user_{Guid.NewGuid():N}".Substring(0, 24);

        var resp = await client.PostAsJsonAsync("/user/password-reset-confirm",
            new PasswordResetConfirm(Shortname: unknown, Email: null, Msisdn: null,
                Otp: "123456", Password: ValidPassword),
            DmartJsonContext.Default.PasswordResetConfirm);
        // Uniform OTP_INVALID error → HTTP 400 — endpoint doesn't leak which leg failed.
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("code mismatch or expired");
    }

    [FactIfPg]
    public async Task WeakPassword_Returns_InvalidPasswordRules()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            var code = await ReadOtpCodeAsync(msisdn);
            code.ShouldNotBeNullOrEmpty();

            // 4 chars, no digit, no uppercase — fails the regex on multiple counts.
            var resp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: code!, Password: "weak"),
                DmartJsonContext.Default.PasswordResetConfirm);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            body.ShouldContain("password does not meet complexity rules");

            // Password-rule failure runs before the OTP probe, so the reset
            // row must still be present.
            (await ReadOtpCodeAsync(msisdn)).ShouldNotBeNullOrEmpty();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task Confirm_With_No_Pending_Otp_Returns_OtpInvalid()
    {
        // Known user but /password-reset-request was never called for them —
        // the reset key has no row, so confirm must fail uniformly. Pins the
        // contract that confirm can never succeed without a paired request.
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            // Sanity: no OTP exists for this user.
            (await ReadOtpCodeAsync(msisdn)).ShouldBeNull();

            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: "123456", Password: ValidPassword),
                DmartJsonContext.Default.PasswordResetConfirm);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            body.ShouldContain("code mismatch or expired");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task BruteForce_Locks_Account_After_MaxFailedAttempts()
    {
        // Wrong OTPs count against the same failed-attempt counter /user/login
        // uses (MaxFailedLoginAttempts, default 5). Without this guarantee a
        // distributed attacker could exhaust the 10^6 6-digit keyspace inside
        // the 300s TTL by hitting the endpoint from many IPs.
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);

            // Submit 4 wrong OTPs — each should return OTP_INVALID (HTTP 400)
            // and the account stays active (counter < 5).
            for (int i = 0; i < 4; i++)
            {
                var bad = await client.PostAsJsonAsync("/user/password-reset-confirm",
                    new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                        Otp: "000000", Password: ValidPassword),
                    DmartJsonContext.Default.PasswordResetConfirm);
                bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"attempt {i + 1} should be OTP_INVALID");
            }

            // 5th wrong OTP trips the lockout — server returns USER_ACCOUNT_LOCKED
            // (HTTP 401) on the same call that flipped IsActive=false.
            var lockResp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: "000000", Password: ValidPassword),
                DmartJsonContext.Default.PasswordResetConfirm);
            lockResp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var lockBody = await lockResp.Content.ReadAsStringAsync();
            lockBody.ShouldContain("Account has been locked");

            // Confirm the lock landed in the DB.
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var locked = await users.GetByShortnameAsync(shortname);
            locked.ShouldNotBeNull();
            locked!.IsActive.ShouldBeFalse();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    // ---- helpers ----

    private async Task<string?> ReadOtpCodeAsync(string dest)
    {
        var repo = _factory.Services.GetRequiredService<OtpRepository>();
        return await repo.GetCodeAsync(ResetPrefix + dest);
    }

    private async Task<(string Shortname, string Email, string Msisdn)> CreateUserAsync(bool withMsisdn)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"pc_test_{suffix}";
        var email = $"{shortname}@test.local";
        var msisdn = $"+9650000{suffix[..8]}";

        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Email = email,
            Msisdn = withMsisdn ? msisdn : null,
            // Pre-set a known starting password so the happy-path test can
            // assert that confirm changed it.
            Password = hasher.Hash("OriginalPass1"),
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user);
        return (shortname, email, msisdn);
    }

    private async Task CleanupAsync(string shortname, string? email, string? msisdn)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(shortname);

            var keys = new List<string>();
            if (!string.IsNullOrEmpty(email)) keys.Add(ResetPrefix + email);
            if (!string.IsNullOrEmpty(msisdn)) keys.Add(ResetPrefix + msisdn);
            if (keys.Count == 0) return;

            var db = _factory.Services.GetRequiredService<Db>();
            await using var conn = await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "DELETE FROM otp WHERE key = ANY($1)", conn);
            cmd.Parameters.Add(new()
            {
                Value = keys.ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort cleanup */ }
    }
}
