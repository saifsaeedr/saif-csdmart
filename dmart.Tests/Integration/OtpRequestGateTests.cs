using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Gate rules for POST /user/otp-request:
//   * A JWT-bearing caller may always request an OTP (e.g. a logged-in user
//     verifying/changing a contact) — UNLESS that account is locked, in which
//     case issuance is refused just like login.
//   * An anonymous caller (no JWT) is gated by is_registrable: registration
//     disabled → no self-service reason to mint an OTP, regardless of whether
//     the supplied identifier maps to an existing user.
public sealed class OtpRequestGateTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OtpRequestGateTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Anonymous_Blocked_When_Not_Registrable_Even_If_User_Exists()
    {
        // is_registrable=false, no JWT. The msisdn maps to an EXISTING user, so
        // the old `user is null && !IsRegistrable` gate would have let this
        // through; the JWT-based gate must block it.
        var factory = NotRegistrable();
        var msisdn = $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}";
        var shortname = await SeedUserAsync(factory, msisdn: msisdn);
        try
        {
            var resp = await factory.CreateClient().PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: msisdn, Email: null),
                DmartJsonContext.Default.SendOTPRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.USERNAME_NOT_EXIST);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Anonymous_Allowed_When_Registrable()
    {
        // is_registrable=true (default), no JWT, brand-new msisdn → allowed and
        // an OTP is minted at the destination.
        var msisdn = $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}";
        var resp = await _factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null),
            DmartJsonContext.Default.SendOTPRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);

        var otpRepo = _factory.Services.GetRequiredService<OtpRepository>();
        (await otpRepo.PeekStoredHashAsync(msisdn)).ShouldNotBeNull("an OTP must be stored at the destination");
    }

    [FactIfPg]
    public async Task Jwt_Allowed_Even_When_Not_Registrable()
    {
        // is_registrable=false but the caller presents a valid JWT → allowed.
        // Old gate blocked this (user lookup by the new msisdn is null AND
        // !IsRegistrable) without ever consulting the JWT.
        var factory = NotRegistrable();
        var user = await _factory.CreateLoggedInUserAsync(factory);
        try
        {
            var msisdn = $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}";
            var resp = await user.Client.PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: msisdn, Email: null),
                DmartJsonContext.Default.SendOTPRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);
        }
        finally { await user.Cleanup(); }
    }

    [FactIfPg]
    public async Task Jwt_Blocked_When_User_Is_Locked()
    {
        // A locked account must not mint an OTP even with a valid JWT. The lock
        // is the attempt-counter lock with is_active=true (a deactivated account
        // can't present a valid JWT — JwtBearerSetup rejects !IsActive), and the
        // session is left intact so the bearer token still validates.
        var user = await _factory.CreateLoggedInUserAsync();
        try
        {
            await SetAttemptCountAsync(user.Shortname, MaxAttempts());

            var msisdn = $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}";
            var resp = await user.Client.PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: msisdn, Email: null),
                DmartJsonContext.Default.SendOTPRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
        }
        finally { await user.Cleanup(); }
    }

    // ---- helpers ----

    private WebApplicationFactory<Program> NotRegistrable() =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.IsRegistrable = false)));

    private int MaxAttempts() =>
        _factory.Services.GetRequiredService<IOptions<Dmart.Config.DmartSettings>>()
            .Value.MaxFailedLoginAttempts;

    private async Task<string> SeedUserAsync(WebApplicationFactory<Program> host, string? msisdn = null)
    {
        var shortname = $"otpgate_{Guid.NewGuid():N}"[..16];
        var users = host.Services.GetRequiredService<UserRepository>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Msisdn = msisdn,
            IsMsisdnVerified = msisdn is not null,
            Type = UserType.Web,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return shortname;
    }

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
