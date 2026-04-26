using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Integration tests covering all the features added in the Python parity
// implementation. Each test hits the live DB through the DmartFactory's
// in-process host — no mocks, no fakes.
public class FullParityTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public FullParityTests(DmartFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token)> LoginAsync()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"Login failed for '{_factory.AdminShortname}': {resp.StatusCode} {raw}");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    // ==================== Login response shape ====================

    [FactIfPg]
    public async Task Login_Returns_Records_Not_Attributes()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        // Python-parity: login returns records[{resource_type,shortname,attributes}]
        body.Records.ShouldNotBeNull();
        body.Records!.Count.ShouldBe(1);
        body.Records[0].ResourceType.ShouldBe(ResourceType.User);
        body.Records[0].Shortname.ShouldBe(_factory.AdminShortname);
        body.Records[0].Attributes!.ShouldContainKey("access_token");
        body.Records[0].Attributes!.ShouldContainKey("type");
        body.Records[0].Attributes!.ShouldContainKey("roles");
        // Attributes dict at the top level should be null (not the old shape)
        body.Attributes.ShouldBeNull();
    }

    // ==================== Session management ====================

    [FactIfPg]
    public async Task Login_Creates_Session_Row_In_DB()
    {
        var (client, _) = await LoginAsync();
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"SELECT COUNT(*) FROM sessions WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = _factory.AdminShortname });
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.ShouldBeGreaterThan(0);
    }

    [FactIfPg]
    public async Task Logout_Clears_Session()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<Dmart.Auth.PasswordHasher>();
        var shortname = $"logout_{Guid.NewGuid():N}"[..20];
        const string password = "LogoutPassword1";
        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(password),
            Type = UserType.Web,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            var loginClient = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            var login = new UserLoginRequest(shortname, null, null, password, null);
            var loginResp = await loginClient.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            var raw = await loginResp.Content.ReadAsStringAsync();
            var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
            var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
                ?? throw new InvalidOperationException($"Login failed for '{shortname}': {loginResp.StatusCode} {raw}");

            var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var logout = await client.PostAsync("/user/logout", null);
            logout.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await users.IsSessionValidAsync(token)).ShouldBeFalse();

            var afterLogout = await client.GetAsync("/info/manifest");
            afterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(shortname); } catch { }
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task Deleted_Session_Rejects_Token_When_Inactivity_Ttl_Disabled()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<Dmart.Auth.PasswordHasher>();
        var shortname = $"sessrev_{Guid.NewGuid():N}"[..20];
        const string password = "SessionPassword1";
        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(password),
            Type = UserType.Web,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            var loginClient = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            var login = new UserLoginRequest(shortname, null, null, password, null);
            var loginResp = await loginClient.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            var raw = await loginResp.Content.ReadAsStringAsync();
            var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
            var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
                ?? throw new InvalidOperationException($"Login failed for '{shortname}': {loginResp.StatusCode} {raw}");

            await users.DeleteSessionAsync(token);
            (await users.IsSessionValidAsync(token)).ShouldBeFalse();

            var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var afterDelete = await client.GetAsync("/info/manifest");
            afterDelete.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(shortname); } catch { }
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    // ==================== Account lockout ====================

    [FactIfPg]
    public async Task Account_Lockout_After_Max_Failed_Attempts()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<Dmart.Auth.PasswordHasher>();
        var db = _factory.Services.GetRequiredService<Db>();

        // Use a throwaway user instead of the admin so a parallel test that
        // logs in as admin can't stumble into the locked state we're setting
        // up here. Previously this test mutated the shared admin row with
        // attempt_count=5, which caused intermittent failures in
        // RecentParityTests.LoginAsync (and similar admin-login helpers) when
        // xUnit ran them concurrently.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"lockout_{suffix}";
        const string password = "CorrectPassword1";
        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(password),
            Type = Dmart.Models.Enums.UserType.Web,
            Language = Dmart.Models.Enums.Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            // Set attempt_count directly to the lockout threshold (5) so the
            // account is already locked. The UpdateAsync path used elsewhere
            // would race with HandleFailedLoginAttempt; direct SQL is
            // deterministic.
            await using (var conn = await db.OpenAsync())
            await using (var cmd = new Npgsql.NpgsqlCommand(
                "UPDATE users SET attempt_count = 5 WHERE shortname = $1", conn))
            {
                cmd.Parameters.Add(new() { Value = shortname });
                await cmd.ExecuteNonQueryAsync();
            }

            var client = _factory.CreateClient();
            // Even correct password should fail with lockout
            var goodLogin = new UserLoginRequest(shortname, null, null, password, null);
            var resp = await client.PostAsJsonAsync("/user/login", goodLogin, DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Error!.Message.ShouldContain("locked");
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(shortname); } catch { }
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    // ==================== Query table routing ====================

    [FactIfPg]
    public async Task Query_Management_Users_Returns_User_ResourceType()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath, SpaceName = "management", Subpath = "/users", Limit = 5,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();
        resp.Records!.ShouldNotBeEmpty();
        foreach (var r in resp.Records) r.ResourceType.ShouldBe(ResourceType.User);
    }

    [FactIfPg]
    public async Task Query_Management_Roles_Returns_Role_ResourceType()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath, SpaceName = "management", Subpath = "/roles", Limit = 5,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();
        foreach (var r in resp.Records!) r.ResourceType.ShouldBe(ResourceType.Role);
    }

    [FactIfPg]
    public async Task Query_Management_Permissions_Returns_Permission_ResourceType()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath, SpaceName = "management", Subpath = "/permissions", Limit = 5,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();
        foreach (var r in resp.Records!) r.ResourceType.ShouldBe(ResourceType.Permission);
    }

    [FactIfPg]
    public async Task Query_History_Returns_History_ResourceType()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.History, SpaceName = "management", Subpath = "/users", Limit = 3,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        if (resp.Records is { Count: > 0 })
            foreach (var r in resp.Records) r.ResourceType.ShouldBe(ResourceType.History);
    }

    [FactIfPg]
    public async Task Query_History_Blocks_Anonymous()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.History, SpaceName = "management", Subpath = "/users", Limit = 1,
        }, actor: null);
        resp.Status.ShouldBe(Status.Failed);
        resp.Error!.Message.ShouldContain("authentication");
    }

    [FactIfPg]
    public async Task Query_Tags_Returns_Aggregated_Tags()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Tags, SpaceName = "management", Subpath = "/", Limit = 50,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        // Tags query returns a single record with tags + tag_counts in attributes.
        if (resp.Records is { Count: > 0 })
        {
            resp.Records[0].Shortname.ShouldBe("tags");
            resp.Records[0].Attributes!.ShouldContainKey("tags");
            resp.Records[0].Attributes!.ShouldContainKey("tag_counts");
        }
    }

    [FactIfPg]
    public async Task Query_Counters_Returns_Empty_Records_With_Total()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Counters, SpaceName = "management", Subpath = "/", Limit = 10,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();
        resp.Records!.Count.ShouldBe(0);
        resp.Attributes!.ShouldContainKey("total");
    }

    // ==================== Query response envelope ====================

    [FactIfPg]
    public async Task Query_Response_Has_Total_And_Returned()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Spaces, SpaceName = "management", Subpath = "/", Limit = 2,
        }, _factory.AdminShortname);
        resp.Attributes.ShouldNotBeNull();
        resp.Attributes!.ShouldContainKey("total");
        resp.Attributes.ShouldContainKey("returned");
        var total = (int)resp.Attributes["total"]!;
        var returned = (int)resp.Attributes["returned"]!;
        returned.ShouldBeLessThanOrEqualTo(2);
        total.ShouldBeGreaterThanOrEqualTo(returned);
    }

    // ==================== Profile completeness ====================

    [FactIfPg]
    public async Task Profile_GET_Returns_All_Python_Parity_Fields()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync("/user/profile");
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        // Profile now returns records[] (Python parity).
        body.Records.ShouldNotBeNull();
        body.Records!.Count.ShouldBeGreaterThan(0);
        var attrs = body.Records![0].Attributes!;
        attrs.ShouldContainKey("email");
        attrs.ShouldContainKey("type");
        attrs.ShouldContainKey("roles");
        attrs.ShouldContainKey("is_email_verified");
        attrs.ShouldContainKey("is_msisdn_verified");
        attrs.ShouldContainKey("force_password_change");
        attrs.ShouldContainKey("permissions");
        // `groups` is intentionally stripped when empty (JsonStripEmptiesMiddleware);
        // the admin test user has no groups, so it's absent. Same conditional-present
        // treatment as the displayname/description/msisdn/payload group below.
        // displayname/description/msisdn/payload are conditional — Python's
        // `if user.X:` guards omit them when the backing value is null.
        // Assert the parity rule instead of unconditional presence: present ⇒ non-empty.
        foreach (var key in new[] { "displayname", "description", "msisdn", "payload", "groups" })
        {
            if (attrs.TryGetValue(key, out var val) && val is string s)
                s.ShouldNotBe("", $"attribute '{key}' present but empty string");
        }
    }

    // ==================== validate_password ====================

    [FactIfPg]
    public async Task ValidatePassword_Requires_Auth()
    {
        var client = _factory.CreateClient();
        // No auth header
        var resp = await client.PostAsync("/user/validate_password",
            new StringContent("{\"password\":\"test\"}", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
    }

    [FactIfPg]
    public async Task ValidatePassword_Correct_Returns_True()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.PostAsync("/user/validate_password",
            new StringContent($"{{\"password\":\"{_factory.AdminPassword}\"}}", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        ((JsonElement)body.Attributes!["valid"]!).GetBoolean().ShouldBeTrue();
    }

    [FactIfPg]
    public async Task ValidatePassword_Wrong_Returns_False()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.PostAsync("/user/validate_password",
            new StringContent("{\"password\":\"wrong-password\"}", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        ((JsonElement)body.Attributes!["valid"]!).GetBoolean().ShouldBeFalse();
    }

    // ==================== check-existing short-circuit response ====================

    [FactIfPg]
    public async Task CheckExisting_ShortCircuits_On_First_Conflict()
    {
        var client = _factory.CreateClient();
        // Shortname exists → returns {"unique": false, "field": "shortname"}
        // without evaluating the email/msisdn params (Python parity).
        var resp = await client.GetAsync($"/user/check-existing?shortname={_factory.AdminShortname}&email=nonexistent@example.com");
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        body.Attributes.ShouldNotBeNull();
        ((JsonElement)body.Attributes!["unique"]!).GetBoolean().ShouldBeFalse();
        ((JsonElement)body.Attributes!["field"]!).GetString().ShouldBe("shortname");
    }

    [FactIfPg]
    public async Task CheckExisting_Returns_Unique_When_All_Free()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/user/check-existing?shortname=nobody_xyz_123&email=nobody_xyz_123@example.com");
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        ((JsonElement)body.Attributes!["unique"]!).GetBoolean().ShouldBeTrue();
        body.Attributes!.ShouldNotContainKey("field");
    }

    // ==================== Correlation ID header ====================

    [FactIfPg]
    public async Task Response_Has_CorrelationId_Header()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/");
        resp.Headers.Contains("X-Correlation-ID").ShouldBeTrue();
    }

    // ==================== User record password stripped ====================

    [FactIfPg]
    public async Task Query_Users_Does_Not_Leak_Password()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath, SpaceName = "management", Subpath = "/users", Limit = 3,
        }, _factory.AdminShortname);
        if (resp.Records is { Count: > 0 })
        {
            foreach (var r in resp.Records)
                r.Attributes!.ShouldNotContainKey("password");
        }
    }

    // ==================== is_registrable enforcement ====================

    [FactIfPg]
    public async Task Create_User_Rejected_Without_Email_Or_Msisdn()
    {
        // Python parity: record.attributes must carry email or msisdn.
        var client = _factory.CreateClient();
        var shortname = "regtest_" + Guid.NewGuid().ToString("N")[..6];
        var body = "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/\",\"attributes\":{\"password\":\"Testtest1234\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Failed);
        result.Error!.Message.ShouldContain("Email or MSISDN");
    }

    // ==================== is_otp_for_create_required enforcement ====================

    [FactIfPg]
    public async Task Create_User_With_Email_And_No_Otp_Is_Rejected_When_Required()
    {
        // Default config has IsOtpForCreateRequired=true. Supplying email
        // without email_otp must produce a structured bad_request.
        var client = _factory.CreateClient();
        var shortname = "otpreq_" + Guid.NewGuid().ToString("N")[..6];
        var body = "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"a@b.c\",\"password\":\"Testtest1234\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Failed);
        result.Error!.Message.ShouldContain("Email OTP");
    }

    [FactIfPg]
    public async Task Create_User_With_Valid_Email_Otp_Succeeds()
    {
        // Pre-store an OTP against the email, then /user/create peeks it
        // (Python parity — verify_user doesn't consume).
        var otpRepo = _factory.Services.GetRequiredService<OtpRepository>();
        var shortname = "otpok_" + Guid.NewGuid().ToString("N")[..6];
        var email = shortname + "@example.test";
        var code = "654321";
        await otpRepo.StoreAsync(email, code, DateTime.UtcNow.AddMinutes(5));

        var client = _factory.CreateClient();
        var body = "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"" + email + "\",\"password\":\"Testtest1234\",\"email_otp\":\"" + code + "\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Success);
        // Python parity: response is records=[Record(attributes={access_token, type})].
        result.Records.ShouldNotBeNull();
        result.Records![0].Attributes!.ShouldContainKey("access_token");

        // /user/create fires resource_folders_creation, which materializes
        // personal/people/{shortname}/* folders owned by the new user. The
        // helper purges those before deleting the user so the FK holds.
        await TestUserCleanup.DeleteUserAndOwnedAsync(_factory.Services, shortname);
    }

    // ==================== session_inactivity_ttl enforcement ====================

    [FactIfPg]
    public async Task Session_Expires_After_Inactivity_Ttl()
    {
        // With SessionInactivityTtl set to 1 second, a login-issued token is
        // accepted, then rejected after the session row ages past 1 second.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
        {
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.SessionInactivityTtl = 1);
        }));
        var client = factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException("login failed");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Works right after login — session is fresh.
        var ok = await client.GetAsync("/info/manifest");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Sleep past the 1-second TTL without touching the session.
        // DB sessions table won't be updated because we're making no requests.
        await Task.Delay(2000);

        // Now the session is stale → TouchSessionAsync evicts it, OnTokenValidated
        // fails the auth context, and JwtBearer returns 401.
        var expired = await client.GetAsync("/info/manifest");
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [FactIfPg]
    public async Task Create_User_With_Email_Succeeds_When_Otp_Check_Disabled()
    {
        // With IsOtpForCreateRequired=false, registration must proceed
        // without any OTP present.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
        {
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.IsOtpForCreateRequired = false);
        }));
        var client = factory.CreateClient();
        var shortname = "otpoff_" + Guid.NewGuid().ToString("N")[..6];
        var body = "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"" + shortname + "@x.y\",\"password\":\"Testtest1234\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Success);

        await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
    }

    // ==================== bot session-inactivity exemption ====================

    [FactIfPg]
    public async Task Bot_Token_Survives_Session_Inactivity_Ttl()
    {
        // Python parity: utils/jwt.py:78,114 — bot users skip the entire
        // session-inactivity machinery. No row is ever created for them at
        // login (so MAX_SESSIONS_PER_USER eviction can't kick them out) and
        // no row is checked at JWT validation (so SESSION_INACTIVITY_TTL
        // can't time them out). This regression test covers both halves.
        //
        // Without the fix, a bot's token would be rejected after the TTL
        // window the same way a web user's is in
        // Session_Expires_After_Inactivity_Ttl above.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
        {
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.SessionInactivityTtl = 1);
        }));

        var users = factory.Services.GetRequiredService<UserRepository>();
        var hasher = factory.Services.GetRequiredService<Dmart.Auth.PasswordHasher>();
        var shortname = "bot_" + Guid.NewGuid().ToString("N")[..8];
        var pwd = "BotTestPwd1234";
        var bot = new Dmart.Models.Core.User
        {
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            Uuid = Guid.NewGuid().ToString("n"),
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true,
            Type = UserType.Bot,
            Password = hasher.Hash(pwd),
        };
        await users.UpsertAsync(bot);

        try
        {
            var client = factory.CreateClient();
            var login = new UserLoginRequest(shortname, null, null, pwd, null);
            var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var raw = await resp.Content.ReadAsStringAsync();
            var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
            var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
                ?? throw new InvalidOperationException("bot login failed");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // No session row should exist for the bot — the login bypasses
            // CreateSessionAsync entirely (Python's set_user_session short-circuit).
            var rows = await users.CountSessionsAsync(shortname);
            rows.ShouldBe(0);

            // Token works immediately.
            var ok = await client.GetAsync("/info/manifest");
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Wait past the 1-second inactivity TTL. A web token would be
            // rejected from this point on (see Session_Expires_After_Inactivity_Ttl).
            await Task.Delay(2000);

            // Bot is exempt — token still works.
            var stillOk = await client.GetAsync("/info/manifest");
            stillOk.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await users.DeleteAsync(shortname);
        }
    }
}
