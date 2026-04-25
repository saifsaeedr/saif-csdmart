using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Confirms firebase_token flows through the login + /user/profile paths
// (Python parity). Each test provisions a throwaway user so cross-class
// parallelism can't step on the admin row, matching InvitationLoginTests'
// pattern.
public sealed class FirebaseTokenTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public FirebaseTokenTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Login_With_FirebaseToken_PersistsOnSession()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            var login = new UserLoginRequest(shortname, null, null, password, null,
                Otp: null, DeviceId: null, FirebaseToken: "fcm-login-token");
            var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var token = await ExtractAccessTokenAsync(resp);
            token.ShouldNotBeNullOrEmpty();

            var stored = await ReadFirebaseTokenAsync(token!);
            stored.ShouldBe("fcm-login-token");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Profile_Update_FirebaseToken_WritesCurrentSessionRow()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            var loginResp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);
            var token = (await ExtractAccessTokenAsync(loginResp))!;

            (await ReadFirebaseTokenAsync(token)).ShouldBeNull();

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var patch = new Dictionary<string, object> { ["firebase_token"] = "fcm-from-profile" };
            var patchResp = await client.PostAsJsonAsync("/user/profile", patch,
                DmartJsonContext.Default.DictionaryStringObject);
            patchResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await ReadFirebaseTokenAsync(token)).ShouldBe("fcm-from-profile");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Profile_Update_FirebaseToken_DoesNotTouchOtherSessions()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            // Two independent login sessions for the same user.
            var client = _factory.CreateClient();
            var loginA = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null,
                    Otp: null, DeviceId: null, FirebaseToken: "fcm-A"),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenA = (await ExtractAccessTokenAsync(loginA))!;

            var loginB = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null,
                    Otp: null, DeviceId: null, FirebaseToken: "fcm-B"),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenB = (await ExtractAccessTokenAsync(loginB))!;

            // Update through session A.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
            var patch = new Dictionary<string, object> { ["firebase_token"] = "fcm-A-updated" };
            var patchResp = await client.PostAsJsonAsync("/user/profile", patch,
                DmartJsonContext.Default.DictionaryStringObject);
            patchResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await ReadFirebaseTokenAsync(tokenA)).ShouldBe("fcm-A-updated");
            (await ReadFirebaseTokenAsync(tokenB)).ShouldBe("fcm-B");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task GetSessionFirebaseTokensAsync_Returns_ActiveTokens()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            // Two sessions: one with a token, one without.
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null,
                    Otp: null, DeviceId: null, FirebaseToken: "fcm-list-1"),
                DmartJsonContext.Default.UserLoginRequest);
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);

            var users = _factory.Services.GetRequiredService<UserRepository>();
            var tokens = await users.GetSessionFirebaseTokensAsync(shortname);
            tokens.ShouldContain("fcm-list-1");
            tokens.ShouldNotContain((string?)null!);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // ---- helpers ----

    private async Task<(string Shortname, string Password)> CreateUserAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"fcm_test_{suffix}";
        var password = "Test1234!fcm";
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
            Password = hasher.Hash(password),
            Roles = new(),
            Groups = new(),
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user);
        return (shortname, password);
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

    private static async Task<string?> ExtractAccessTokenAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString();
    }

    // Read sessions.firebase_token directly — the repository exposes only the
    // write paths and a list-by-shortname read, both of which this test
    // exercises elsewhere.
    private async Task<string?> ReadFirebaseTokenAsync(string token)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT firebase_token FROM sessions WHERE token = $1", conn);
        cmd.Parameters.Add(new() { Value = token });
        var raw = await cmd.ExecuteScalarAsync();
        return raw is string s ? s : null;
    }
}
