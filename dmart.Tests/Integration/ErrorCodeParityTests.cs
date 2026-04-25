using System.Net;
using System.Net.Http.Json;
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

// Asserts Python-exact (type, code) for four error conditions that the C# port
// previously silently failed on: USER_ISNT_VERIFIED, INVALID_PASSWORD_RULES,
// INVALID_ROUTE, and CANNT_DELETE.
public sealed class ErrorCodeParityTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ErrorCodeParityTests(DmartFactory factory) => _factory = factory;

    // ---------- USER_ACCOUNT_LOCKED (inactive user on login) ----------

    [FactIfPg]
    public async Task Login_InactiveUser_Returns_USER_ACCOUNT_LOCKED()
    {
        // Python parity: router.py:504-508 — `is_active=false` surfaces as
        // USER_ACCOUNT_LOCKED(110) with "Account has been locked.", NOT
        // USER_ISNT_VERIFIED. The verified code is reserved for the OTP /
        // verification flow, not the login path.
        var (shortname, pw) = await CreateInactiveUserAsync("correct-pw");
        try
        {
            var client = _factory.CreateClient();
            var login = new UserLoginRequest(shortname, null, null, pw, null);
            var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Error.ShouldNotBeNull();
            body.Error!.Type.ShouldBe("auth");
            body.Error.Code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            body.Error.Message.ShouldBe("Account has been locked.");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // ---------- INVALID_PASSWORD_RULES ----------

    [FactIfPg]
    public async Task UpdateProfile_WeakPassword_Returns_INVALID_PASSWORD_RULES()
    {
        var (shortname, pw) = await CreateActiveUserAsync("ValidPassword1");
        try
        {
            var client = _factory.CreateClient();
            var login = new UserLoginRequest(shortname, null, null, pw, null);
            var loginResp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            loginResp.EnsureSuccessStatusCode();
            var loginBody = await loginResp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            var token = loginBody!.Records!.First().Attributes!["access_token"]!.ToString();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            // "weak" has no uppercase + no digit — fails Python's PASSWORD regex.
            var patch = new Dictionary<string, object>
            {
                ["password"] = "weak",
                ["old_password"] = pw,
            };
            var req = new HttpRequestMessage(HttpMethod.Post, "/user/profile")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(patch, DmartJsonContext.Default.DictionaryStringObject),
                    Encoding.UTF8, "application/json"),
            };
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.INVALID_PASSWORD_RULES);
            body.Error.Type.ShouldBe("jwtauth");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task UpdateProfile_StrongPassword_Succeeds()
    {
        var (shortname, pw) = await CreateActiveUserAsync("OldPassword1");
        try
        {
            var client = _factory.CreateClient();
            var login = new UserLoginRequest(shortname, null, null, pw, null);
            var loginResp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            var loginBody = await loginResp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            var token = loginBody!.Records!.First().Attributes!["access_token"]!.ToString();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var patch = new Dictionary<string, object>
            {
                ["password"] = "NewPassword9",   // 8+ chars, has digit + uppercase
                ["old_password"] = pw,
            };
            var req = new HttpRequestMessage(HttpMethod.Post, "/user/profile")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(patch, DmartJsonContext.Default.DictionaryStringObject),
                    Encoding.UTF8, "application/json"),
            };
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // ---------- INVALID_ROUTE ----------

    [FactIfPg]
    public async Task UnknownRoute_Returns_INVALID_ROUTE_230()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/this/is/not/a/real/endpoint");
        ((int)resp.StatusCode).ShouldBe(422);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.INVALID_ROUTE);
        body.Error.Type.ShouldBe("request");
    }

    // ---------- CANNT_DELETE ----------

    [FactIfPg]
    public async Task DeleteManagementSpace_Returns_CANNT_DELETE()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var loginResp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var loginBody = await loginResp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var token = loginBody!.Records!.First().Attributes!["access_token"]!.ToString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var body = new
        {
            space_name = "management",
            request_type = "delete",
            records = new[] {
                new { resource_type = "space", subpath = "/", shortname = "management", attributes = new { } }
            },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/managed/request")
        {
            Content = new StringContent(
                """{"space_name":"management","request_type":"delete","records":[{"resource_type":"space","subpath":"/","shortname":"management","attributes":{}}]}""",
                Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        // Python returns 400 with aggregate failed_records; our failed_records list
        // carries the per-record error_code we need to assert.
        respBody.ShouldContain(InternalErrorCode.CANNT_DELETE.ToString());
    }

    // ---------- helpers ----------

    private async Task<(string Shortname, string Password)> CreateInactiveUserAsync(string password)
        => await CreateUserInternalAsync(password, isActive: false);

    private async Task<(string Shortname, string Password)> CreateActiveUserAsync(string password)
        => await CreateUserInternalAsync(password, isActive: true);

    private async Task<(string Shortname, string Password)> CreateUserInternalAsync(string password, bool isActive)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"parity_{suffix}";
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = isActive,
            Password = hasher.Hash(password),
            Type = UserType.Web,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
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
}
