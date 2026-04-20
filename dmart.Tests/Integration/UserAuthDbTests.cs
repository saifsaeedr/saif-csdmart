using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// DB-backed integration tests. Skip cleanly when DMART_TEST_PG_CONN is unset so the
// suite can still run on a developer machine without PostgreSQL.
//
// Joins SharedAdminState so the wrong-password + attempt_count-mutating tests below
// don't race with other classes that also touch the admin row.
[Collection(SharedAdminStateCollection.Name)]
public class UserAuthDbTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserAuthDbTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Bootstrap_Admin_Can_Login()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body.ShouldNotBeNull();
        body!.Status.ShouldBe(Status.Success);
        // Login now returns records[{attributes: {access_token}}] (Python parity).
        body.Records.ShouldNotBeNull();
        body.Records!.Count.ShouldBeGreaterThan(0);
        body.Records![0].Attributes.ShouldNotBeNull();
        body.Records![0].Attributes!.ContainsKey("access_token").ShouldBeTrue();
    }

    [FactIfPg]
    public async Task Wrong_Password_Returns_401()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, "definitely-wrong", null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Reset attempt counter so this test's failed login doesn't lock out
        // cstest for other tests (account lockout enforcement is now active).
        var users = _factory.Services.GetRequiredService<Dmart.DataAdapters.Sql.UserRepository>();
        await users.ResetAttemptsAsync(_factory.AdminShortname);
    }

    [FactIfPg]
    public async Task Authenticated_Me_Returns_Identity()
    {
        var client = _factory.CreateClient();
        var token = await LoginAdminAndGetTokenAsync(client);
        token.ShouldNotBeNull();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.GetAsync("/info/me");
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var wwwAuth = string.Join("; ", resp.Headers.WwwAuthenticate.Select(h => h.ToString()));
            throw new Xunit.Sdk.XunitException($"expected OK, got {resp.StatusCode}; WWW-Authenticate: {wwwAuth}");
        }
    }

    [FactIfPg]
    public async Task Profile_Returns_Admin_Details_When_Authenticated()
    {
        var client = _factory.CreateClient();
        var token = await LoginAdminAndGetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/user/profile");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        // Profile now returns records[] (Python parity), not attributes{}.
        body.Records.ShouldNotBeNull();
        body.Records![0].Shortname.ShouldBe(_factory.AdminShortname);
    }

    [FactIfPg]
    public async Task Profile_Does_Not_Return_Empty_String_For_Null_Optional_Fields()
    {
        var client = _factory.CreateClient();
        var token = await LoginAdminAndGetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/user/profile");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Records.ShouldNotBeNull();
        var attrs = body.Records![0].Attributes;
        attrs.ShouldNotBeNull();

        // Python parity: optional user fields must be absent when the backing
        // value is null — never present as "" or null. Regression: ProfileHandler
        // previously coerced nulls to empty strings.
        foreach (var key in new[] { "email", "msisdn", "displayname", "description", "payload" })
        {
            if (attrs!.TryGetValue(key, out var val))
            {
                val.ShouldNotBeNull($"attribute '{key}' present but null");
                if (val is string s)
                    s.ShouldNotBe("", $"attribute '{key}' present but empty string");
            }
        }
    }

    [FactIfPg]
    public async Task Check_Existing_Returns_Conflict_For_Admin()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/user/check-existing?shortname={_factory.AdminShortname}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        // Python-parity: {"unique": false, "field": "shortname"} on first conflict.
        ((JsonElement)body!.Attributes!["unique"]!).GetBoolean().ShouldBeFalse();
        ((JsonElement)body.Attributes!["field"]!).GetString().ShouldBe("shortname");
    }

    [FactIfPg]
    public async Task Successful_Login_Resets_AttemptCount_To_Zero()
    {
        var users = _factory.Services.GetRequiredService<Dmart.DataAdapters.Sql.UserRepository>();
        var db = _factory.Services.GetRequiredService<Dmart.DataAdapters.Sql.Db>();

        // Seed: admin row with attempt_count=3 (simulating 3 prior failed tries
        // that didn't cross the lockout threshold).
        await using (var conn = await db.OpenAsync())
        await using (var cmd = new Npgsql.NpgsqlCommand(
            "UPDATE users SET attempt_count = 3 WHERE shortname = $1", conn))
        {
            cmd.Parameters.Add(new() { Value = _factory.AdminShortname });
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var client = _factory.CreateClient();
            var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
            var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Verify attempt_count was reset to 0 and persisted (not overwritten
            // by the follow-up UpsertAsync in ProcessLoginAsync).
            await using var conn = await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT attempt_count FROM users WHERE shortname = $1", conn);
            cmd.Parameters.Add(new() { Value = _factory.AdminShortname });
            var stored = await cmd.ExecuteScalarAsync();
            var count = stored is int i ? i : (stored is null || stored is DBNull ? (int?)null : Convert.ToInt32(stored));
            (count ?? 0).ShouldBe(0);
        }
        finally
        {
            await users.ResetAttemptsAsync(_factory.AdminShortname);
        }
    }

    private async Task<string?> LoginAdminAndGetTokenAsync(HttpClient client)
    {
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        // Login now returns records[{attributes: {access_token}}] (Python parity).
        return body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString();
    }
}
