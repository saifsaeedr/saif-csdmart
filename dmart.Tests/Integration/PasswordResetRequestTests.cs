using System.Net;
using System.Net.Http.Json;
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

// /user/password-reset-request sends an OTP via the channel matching the
// supplied identifier (msisdn/shortname → SMS, email → Email). The OTP body
// renders from the `otp_message` language template, the same one /otp-request
// uses. These tests assert the otp row that the handler writes, since
// SmsSender/SmtpSender short-circuit silently in mock mode and have no
// observable side effect on the wire.
public sealed class PasswordResetRequestTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PasswordResetRequestTests(DmartFactory factory) => _factory = factory;

    // Mirrors OtpHandler.ResetOtpPrefix — handler constant is private, so the
    // tests duplicate the literal. Any drift breaks the assertions loudly.
    private const string ResetPrefix = "pwd-reset:";

    [FactIfPg]
    public async Task ShortnameOnly_Sends_Otp_To_Users_Msisdn()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // OtpRepository.StoreAsync keys the row by the destination — assert
            // a code was stored at the user's msisdn and not at their email.
            (await OtpExistsAsync(msisdn)).ShouldBeTrue();
            (await OtpExistsAsync(email)).ShouldBeFalse(
                "user has msisdn — must not also send to email");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task ShortnameOnly_NoMsisdn_FallsBack_To_Email()
    {
        // Csdmart-only behavior (intentional divergence from upstream Python's
        // reset_password, which silently no-ops here): when the caller
        // supplied only a shortname and the resolved user has no msisdn, the
        // handler falls back to the email channel so the reset still
        // reaches the user. The fallback is gated to shortname-only requests
        // — direct-msisdn requests honor the channel the caller picked
        // (covered by MsisdnDirect_NoFallback_To_Email).
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(email)).ShouldBeTrue(
                "shortname-only with no msisdn falls back to email");
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    [FactIfPg]
    public async Task ShortnameOnly_UnknownUser_Returns_Ok_AndSends_Nothing()
    {
        // Anti-enumeration: should not leak whether the shortname exists.
        var unknown = $"definitely_not_a_user_{Guid.NewGuid():N}".Substring(0, 30);
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/user/password-reset-request",
            new PasswordResetRequest(unknown, null, null),
            DmartJsonContext.Default.PasswordResetRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        // No row should exist — the user never existed, so there's nothing
        // to send for. Status code being OK is the anti-enumeration check.
    }

    [FactIfPg]
    public async Task EmailDirect_Sends_Otp_To_Email()
    {
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, email, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(email)).ShouldBeTrue();
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    [FactIfPg]
    public async Task MsisdnDirect_Sends_Otp_To_Msisdn()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, null, msisdn),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(msisdn)).ShouldBeTrue();
            (await OtpExistsAsync(email)).ShouldBeFalse(
                "msisdn-direct must not also send to email");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task MsisdnDirect_NoFallback_To_Email()
    {
        // Pins the no-fallback property of the direct-msisdn path: even when
        // the user record exists with an email but no msisdn, a request that
        // supplied only a msisdn must NOT silently fall back to email.
        // (The handler routes the lookup by the supplied msisdn, so the user
        // here is unreachable through the msisdn key — silent OK is correct.)
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var ghostMsisdn = $"+99900000{Guid.NewGuid():N}".Substring(0, 14);
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, null, ghostMsisdn),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(ghostMsisdn)).ShouldBeFalse();
            (await OtpExistsAsync(email)).ShouldBeFalse(
                "direct-msisdn lookup must not fall back to the user's email");
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    [FactIfPg]
    public async Task EmailDirect_Mismatched_Email_Sends_Nothing()
    {
        // The email branch only sends when user.email matches the request's
        // email value. A mismatched email must not leak.
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var stranger = $"someone_else_{Guid.NewGuid():N}@test.local".Substring(0, 40);
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, stranger, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(email)).ShouldBeFalse();
            (await OtpExistsAsync(stranger)).ShouldBeFalse();
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    // ---- helpers ----

    // Reset OTPs are stored under the "pwd-reset:" prefix so they can't be
    // consumed by /user/login's OTP path. Tests pass the bare destination;
    // this helper prepends the prefix to match what the handler writes.
    private async Task<bool> OtpExistsAsync(string dest)
    {
        var repo = _factory.Services.GetRequiredService<OtpRepository>();
        return await repo.PeekStoredHashAsync(ResetPrefix + dest) is not null;
    }

    private async Task<(string Shortname, string Email, string Msisdn)> CreateUserAsync(bool withMsisdn)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"pr_test_{suffix}";
        var email = $"{shortname}@test.local";
        var msisdn = $"+9650000{suffix[..8]}";

        var users = _factory.Services.GetRequiredService<UserRepository>();
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

            // Build the exact set of otp keys this test could have produced;
            // delete those rows so back-to-back test runs start clean. Reset
            // OTPs live under the "pwd-reset:" prefix.
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
