using System.Net;
using System.Net.Http.Json;
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

// Pins the contract that UserRepository.TouchLoginAsync (+ its caller in
// UserService.PerformLoginAsync) maintains:
//   - device_id and last_login.timestamp are written after a successful login
//   - columns the login flow does NOT own (notably `payload`, but also email,
//     language, etc.) survive a concurrent plugin write that lands between the
//     auth check and post-login bookkeeping
//   - early-return when caller passes (null, null) — no SQL round-trip, no
//     accidental updated_at bump
//
// The plugin-race scenario is the load-bearing one: replacing UpsertAsync with
// TouchLoginAsync was the whole point of #41. If this test ever fails, the
// regression is the original bug (login bookkeeping clobbers concurrent
// plugin writes by replaying the pre-login in-memory snapshot).
public sealed class TouchLoginPostAuthTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public TouchLoginPostAuthTests(DmartFactory factory) => _factory = factory;

    // Direct UserRepository.TouchLoginAsync exercise — no HTTP, no auth.
    // Pins the COALESCE-preserves-on-null semantics + the early-return guard.
    [FactIfPg]
    public async Task TouchLoginAsync_WritesDeviceIdAndLastLogin_LeavesOtherColumnsAlone()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var sn = "tl_touch_" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserAsync(users, sn,
            email: $"{sn}@before.test",
            payload: new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse("""{"a":1}""").RootElement.Clone(),
            });

        try
        {
            var lastLogin = new Dictionary<string, object>
            {
                ["timestamp"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["headers"] = new Dictionary<string, object> { ["user-agent"] = "ua-test" },
            };
            await users.TouchLoginAsync(sn, deviceId: "device-1", lastLogin: lastLogin);

            var after = await users.GetByShortnameAsync(sn);
            after.ShouldNotBeNull();
            after!.DeviceId.ShouldBe("device-1");
            after.LastLogin.ShouldNotBeNull();
            after.LastLogin!.ContainsKey("timestamp").ShouldBeTrue();

            // The fields the login flow does NOT own must survive untouched —
            // this is the regression-pinning assertion that distinguishes
            // TouchLoginAsync from a full-row UpsertAsync.
            after.Email.ShouldBe($"{sn}@before.test");
            after.Payload.ShouldNotBeNull();
            after.Payload!.Body.ShouldNotBeNull();
            var body = after.Payload.Body!.Value;
            body.ValueKind.ShouldBe(JsonValueKind.Object);
            body.GetProperty("a").GetInt32().ShouldBe(1);
        }
        finally { await TryDeleteAsync(users, sn); }
    }

    [FactIfPg]
    public async Task TouchLoginAsync_BothNull_IsNoOp_NoUpdatedAtBump()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var sn = "tl_noop_" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserAsync(users, sn, email: $"{sn}@stable.test");

        try
        {
            var before = await users.GetByShortnameAsync(sn);
            before.ShouldNotBeNull();
            var beforeUpdatedAt = before!.UpdatedAt;

            // Early-return path — passing (null, null) must not touch the row
            // at all (no UPDATE statement, no updated_at bump). Sleep so a
            // bug that DID issue an UPDATE would produce a measurably newer
            // timestamp than the seeded row.
            await Task.Delay(50);
            await users.TouchLoginAsync(sn, deviceId: null, lastLogin: null);

            var after = await users.GetByShortnameAsync(sn);
            after!.UpdatedAt.ShouldBe(beforeUpdatedAt,
                "TouchLoginAsync(null, null) must early-return without issuing an UPDATE");
            after.DeviceId.ShouldBeNull();
        }
        finally { await TryDeleteAsync(users, sn); }
    }

    // The regression scenario that motivated #41: a plugin's after-hook
    // (e.g. OAuth) writes a Payload via UpsertWithPriorAsync between the
    // auth check and post-login bookkeeping. With the old UpsertAsync path,
    // the bookkeeping replayed the pre-login in-memory snapshot and erased
    // the plugin's Payload. TouchLoginAsync must NOT do that.
    //
    // We simulate the race deterministically: log in (which runs the post-
    // login TouchLoginAsync), then have a "plugin" write via
    // UpsertWithPriorAsync, then log in again (so the second TouchLoginAsync
    // runs AFTER the plugin write and must not erase it). The second login
    // is the actual race window the test pins.
    [FactIfPg]
    public async Task Login_TouchLogin_DoesNotErase_ConcurrentPluginPayloadWrite()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        try
        {
            var client = _factory.CreateClient();

            // First login — to establish a session and exercise the
            // TouchLoginAsync write path once.
            var login1 = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(sn, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);
            login1.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Plugin writes a Payload via UpsertWithPriorAsync (the path
            // native plugins take through NativePluginCallbacks.EmitUpdateUser).
            // The in-memory snapshot the next login's UserService holds will
            // NOT include this Payload — the regression bug was that the
            // subsequent UpsertAsync would replay the snapshot and erase it.
            var users = sp.GetRequiredService<UserRepository>();
            var current = await users.GetByShortnameAsync(sn);
            current.ShouldNotBeNull();
            var pluginPayload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse("""{"oauth_provider":"google","sub":"abc123"}""")
                    .RootElement.Clone(),
            };
            await users.UpsertWithPriorAsync(current! with { Payload = pluginPayload });

            // Second login — the load-bearing one. UserService re-reads the
            // user, runs the auth check, then calls TouchLoginAsync. The
            // payload the plugin wrote above must survive.
            var login2 = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(sn, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);
            login2.StatusCode.ShouldBe(HttpStatusCode.OK);

            var after = await users.GetByShortnameAsync(sn);
            after.ShouldNotBeNull();
            after!.Payload.ShouldNotBeNull(
                "plugin-written Payload must survive the post-login TouchLoginAsync");
            after.Payload!.Body.ShouldNotBeNull();
            var body = after.Payload.Body!.Value;
            body.GetProperty("oauth_provider").GetString().ShouldBe("google");
            body.GetProperty("sub").GetString().ShouldBe("abc123");

            // And the login bookkeeping that TouchLoginAsync owns still landed.
            after.LastLogin.ShouldNotBeNull();
        }
        finally
        {
            var users = sp.GetRequiredService<UserRepository>();
            await TryDeleteAsync(users, sn);
        }
    }

    // ----- helpers -----

    private static async Task SeedUserAsync(
        UserRepository users, string sn,
        string? email = null, Payload? payload = null)
    {
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn,
            SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true,
            Email = email, IsEmailVerified = true,
            Payload = payload,
            Roles = new(), Groups = new(),
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    // Creates a user with a real password so the /user/login round-trip
    // succeeds. Distinct from SeedUserAsync (which leaves Password null
    // for the direct-repository tests above).
    private static async Task<(string Shortname, string Password)> CreateLoginCapableUserAsync(
        IServiceProvider sp)
    {
        var sn = "tl_lr_" + Guid.NewGuid().ToString("N")[..10];
        var password = "Test1234!tl";
        var users = sp.GetRequiredService<UserRepository>();
        var hasher = sp.GetRequiredService<PasswordHasher>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn,
            SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true,
            Password = hasher.Hash(password),
            Email = $"{sn}@test.local", IsEmailVerified = true,
            Roles = new(), Groups = new(),
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return (sn, password);
    }

    private static async Task TryDeleteAsync(UserRepository users, string sn)
    {
        try
        {
            await users.DeleteAllSessionsAsync(sn);
            await users.DeleteAsync(sn);
        }
        catch { /* best effort */ }
    }
}
