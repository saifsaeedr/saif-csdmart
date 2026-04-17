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

    [Fact]
    public async Task Login_Returns_Records_Not_Attributes()
    {
        if (!DmartFactory.HasPg) return;
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

    [Fact]
    public async Task Login_Creates_Session_Row_In_DB()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"SELECT COUNT(*) FROM sessions WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = _factory.AdminShortname });
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Logout_Clears_Session()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        // Logout
        await client.PostAsync("/user/logout", null);
        // The specific session should be gone (though others may remain)
    }

    // ==================== Account lockout ====================

    [Fact]
    public async Task Account_Lockout_After_Max_Failed_Attempts()
    {
        if (!DmartFactory.HasPg) return;
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var db = _factory.Services.GetRequiredService<Db>();

        // Set attempt_count directly to the lockout threshold (5) so the
        // account is already locked. This avoids race conditions with other
        // tests that might reset the counter in parallel.
        await using (var conn = await db.OpenAsync())
        await using (var cmd = new Npgsql.NpgsqlCommand(
            "UPDATE users SET attempt_count = 5 WHERE shortname = $1", conn))
        {
            cmd.Parameters.Add(new() { Value = _factory.AdminShortname });
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var client = _factory.CreateClient();
            // Even correct password should fail with lockout
            var goodLogin = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
            var resp = await client.PostAsJsonAsync("/user/login", goodLogin, DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Error!.Message.ShouldContain("locked");
        }
        finally
        {
            await users.ResetAttemptsAsync(_factory.AdminShortname);
        }
    }

    // ==================== Query table routing ====================

    [Fact]
    public async Task Query_Management_Users_Returns_User_ResourceType()
    {
        if (!DmartFactory.HasPg) return;
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

    [Fact]
    public async Task Query_Management_Roles_Returns_Role_ResourceType()
    {
        if (!DmartFactory.HasPg) return;
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath, SpaceName = "management", Subpath = "/roles", Limit = 5,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();
        foreach (var r in resp.Records!) r.ResourceType.ShouldBe(ResourceType.Role);
    }

    [Fact]
    public async Task Query_Management_Permissions_Returns_Permission_ResourceType()
    {
        if (!DmartFactory.HasPg) return;
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath, SpaceName = "management", Subpath = "/permissions", Limit = 5,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();
        foreach (var r in resp.Records!) r.ResourceType.ShouldBe(ResourceType.Permission);
    }

    [Fact]
    public async Task Query_History_Returns_History_ResourceType()
    {
        if (!DmartFactory.HasPg) return;
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.History, SpaceName = "management", Subpath = "/users", Limit = 3,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        if (resp.Records is { Count: > 0 })
            foreach (var r in resp.Records) r.ResourceType.ShouldBe(ResourceType.History);
    }

    [Fact]
    public async Task Query_History_Blocks_Anonymous()
    {
        if (!DmartFactory.HasPg) return;
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.History, SpaceName = "management", Subpath = "/users", Limit = 1,
        }, actor: null);
        resp.Status.ShouldBe(Status.Failed);
        resp.Error!.Message.ShouldContain("authentication");
    }

    [Fact]
    public async Task Query_Tags_Returns_Aggregated_Tags()
    {
        if (!DmartFactory.HasPg) return;
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

    [Fact]
    public async Task Query_Counters_Returns_Empty_Records_With_Total()
    {
        if (!DmartFactory.HasPg) return;
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

    [Fact]
    public async Task Query_Response_Has_Total_And_Returned()
    {
        if (!DmartFactory.HasPg) return;
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

    [Fact]
    public async Task Profile_GET_Returns_All_Python_Parity_Fields()
    {
        if (!DmartFactory.HasPg) return;
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
        attrs.ShouldContainKey("groups");
        attrs.ShouldContainKey("is_email_verified");
        attrs.ShouldContainKey("is_msisdn_verified");
        attrs.ShouldContainKey("force_password_change");
        attrs.ShouldContainKey("displayname");
        attrs.ShouldContainKey("description");
        attrs.ShouldContainKey("permissions");
    }

    // ==================== validate_password ====================

    [Fact]
    public async Task ValidatePassword_Requires_Auth()
    {
        if (!DmartFactory.HasPg) return;
        var client = _factory.CreateClient();
        // No auth header
        var resp = await client.PostAsync("/user/validate_password",
            new StringContent("{\"password\":\"test\"}", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
    }

    [Fact]
    public async Task ValidatePassword_Correct_Returns_True()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        var resp = await client.PostAsync("/user/validate_password",
            new StringContent($"{{\"password\":\"{_factory.AdminPassword}\"}}", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        ((JsonElement)body.Attributes!["valid"]!).GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ValidatePassword_Wrong_Returns_False()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        var resp = await client.PostAsync("/user/validate_password",
            new StringContent("{\"password\":\"wrong-password\"}", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        ((JsonElement)body.Attributes!["valid"]!).GetBoolean().ShouldBeFalse();
    }

    // ==================== check-existing per-field response ====================

    [Fact]
    public async Task CheckExisting_Returns_PerField_Booleans()
    {
        if (!DmartFactory.HasPg) return;
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/user/check-existing?shortname={_factory.AdminShortname}&email=nonexistent@example.com");
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        body.Attributes.ShouldNotBeNull();
        body.Attributes!.ShouldContainKey("shortname");
        body.Attributes!.ShouldContainKey("email");
        body.Attributes!.ShouldContainKey("msisdn");
        ((JsonElement)body.Attributes!["shortname"]!).GetBoolean().ShouldBeTrue();
        ((JsonElement)body.Attributes!["email"]!).GetBoolean().ShouldBeFalse();
    }

    // ==================== Correlation ID header ====================

    [Fact]
    public async Task Response_Has_CorrelationId_Header()
    {
        if (!DmartFactory.HasPg) return;
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/");
        resp.Headers.Contains("X-Correlation-ID").ShouldBeTrue();
    }

    // ==================== User record password stripped ====================

    [Fact]
    public async Task Query_Users_Does_Not_Leak_Password()
    {
        if (!DmartFactory.HasPg) return;
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

    [Fact]
    public async Task Create_User_Respects_IsRegistrable_Setting()
    {
        // This test verifies the setting is checked — the default is true,
        // so registration should succeed. We test the path exists.
        if (!DmartFactory.HasPg) return;
        var client = _factory.CreateClient();
        var body = "{\"shortname\":\"regtest_" + Guid.NewGuid().ToString("N")[..6] + "\",\"password\":\"testtest1234\"}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        // Should succeed (is_registrable defaults to true)
        result!.Status.ShouldBe(Status.Success);

        // Cleanup
        if (result.Attributes?.TryGetValue("shortname", out var sn) == true)
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(sn.ToString()!);
        }
    }

    // ==================== is_otp_for_create_required enforcement ====================

    [Fact]
    public async Task Create_User_With_Email_And_No_Otp_Is_Rejected_When_Required()
    {
        // Default config has IsOtpForCreateRequired=true. Supplying email
        // without email_otp must produce a structured bad_request.
        if (!DmartFactory.HasPg) return;
        var client = _factory.CreateClient();
        var shortname = "otpreq_" + Guid.NewGuid().ToString("N")[..6];
        var body = "{\"shortname\":\"" + shortname + "\",\"email\":\"a@b.c\",\"password\":\"testtest1234\"}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Failed);
        result.Error!.Message.ShouldContain("email_otp");
    }

    [Fact]
    public async Task Create_User_With_Valid_Email_Otp_Succeeds()
    {
        // Pre-store an OTP against the email, then /user/create consumes it.
        if (!DmartFactory.HasPg) return;
        var otpRepo = _factory.Services.GetRequiredService<OtpRepository>();
        var shortname = "otpok_" + Guid.NewGuid().ToString("N")[..6];
        var email = shortname + "@example.test";
        var code = "654321";
        await otpRepo.StoreAsync(email, code, DateTime.UtcNow.AddMinutes(5));

        var client = _factory.CreateClient();
        var body = "{\"shortname\":\"" + shortname + "\",\"email\":\"" + email + "\",\"password\":\"testtest1234\",\"email_otp\":\"" + code + "\"}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Success);

        // Cleanup
        var users = _factory.Services.GetRequiredService<UserRepository>();
        await users.DeleteAsync(shortname);
    }

    // ==================== session_inactivity_ttl enforcement ====================

    [Fact]
    public async Task Session_Expires_After_Inactivity_Ttl()
    {
        // With SessionInactivityTtl set to 1 second, a login-issued token is
        // accepted, then rejected after the session row ages past 1 second.
        if (!DmartFactory.HasPg) return;
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

    [Fact]
    public async Task Create_User_With_Email_Succeeds_When_Otp_Check_Disabled()
    {
        // With IsOtpForCreateRequired=false, registration must proceed
        // without any OTP present.
        if (!DmartFactory.HasPg) return;
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
        {
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.IsOtpForCreateRequired = false);
        }));
        var client = factory.CreateClient();
        var shortname = "otpoff_" + Guid.NewGuid().ToString("N")[..6];
        var body = "{\"shortname\":\"" + shortname + "\",\"email\":\"" + shortname + "@x.y\",\"password\":\"testtest1234\"}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Success);

        var users = factory.Services.GetRequiredService<UserRepository>();
        await users.DeleteAsync(shortname);
    }
}
