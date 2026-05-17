using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins.Native;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the host-side EmitGetSessionFirebaseTokens callback shape:
//   - returns a JSON array string, never null
//   - empty shortname short-circuits to "[]" without touching the DB
//   - users with no sessions get "[]"
//   - users with sessions get the firebase_token values back
//   - exceptions in the repo path collapse to "[]" (deliberate deviation
//     from LoadEntry/LoadUser's JSON error envelope — see method comment)
//
// Calls EmitGetSessionFirebaseTokens directly so we exercise the typed path
// without synthesising UTF-8 byte buffers — mirrors PluginCallbackHistoryTests'
// EmitSaveEntry / EmitUpdateUser approach.
[Collection(PluginInvocationContextCollection.Name)]
public sealed class NativePluginGetFirebaseTokensCallbackTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public NativePluginGetFirebaseTokensCallbackTests(DmartFactory factory) => _factory = factory;

    // Empty shortname is the cheap short-circuit path — must NOT open a DI
    // scope or hit the DB. Verified indirectly here (the empty-shortname
    // branch returns before Services is read), and explicitly in the
    // unit-test variant in NativePluginGetFirebaseTokensUnitTests.
    [Fact]
    public void EmitGetSessionFirebaseTokens_EmptyShortname_Returns_EmptyArray_Without_DB()
    {
        NativePluginCallbacks.EmitGetSessionFirebaseTokens("", null, logger: null)
            .ShouldBe("[]");
        NativePluginCallbacks.EmitGetSessionFirebaseTokens("", inactivityTtlSeconds: 60, logger: null)
            .ShouldBe("[]");
    }

    [FactIfPg]
    public async Task EmitGetSessionFirebaseTokens_NoSessions_Returns_EmptyArray()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var sn = "fcm_cb_none_" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserAsync(users, sn);

        try
        {
            var json = NativePluginCallbacks.EmitGetSessionFirebaseTokens(sn, null, logger: null);

            // The serialized JSON shape must always be a JSON array — never null,
            // never an object. Plugin callers parse the result as List<string>;
            // a null or object payload would corrupt them silently.
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
            doc.RootElement.GetArrayLength().ShouldBe(0);
        }
        finally
        {
            await TryDeleteAsync(users, sn);
        }
    }

    [FactIfPg]
    public async Task EmitGetSessionFirebaseTokens_WithSessions_Returns_TokenList()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var sn = "fcm_cb_some_" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserAsync(users, sn);

        try
        {
            // Two sessions with firebase tokens + one without (firebase_token IS
            // NULL) — only the two non-null tokens should come back.
            await users.CreateSessionAsync(sn, token: "tok-a", firebaseToken: "fcm-A");
            await users.CreateSessionAsync(sn, token: "tok-b", firebaseToken: "fcm-B");
            await users.CreateSessionAsync(sn, token: "tok-c", firebaseToken: null);

            var json = NativePluginCallbacks.EmitGetSessionFirebaseTokens(sn, null, logger: null);

            using var doc = JsonDocument.Parse(json);
            doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
            var tokens = doc.RootElement.EnumerateArray()
                .Select(e => e.GetString()).ToList();
            tokens.Count.ShouldBe(2);
            tokens.ShouldContain("fcm-A");
            tokens.ShouldContain("fcm-B");
            tokens.ShouldNotContain((string?)null!);
        }
        finally
        {
            await TryDeleteAsync(users, sn);
        }
    }

    // ----- helpers -----

    private static async Task SeedUserAsync(UserRepository users, string sn)
    {
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn,
            SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true,
            Email = $"{sn}@test.local", IsEmailVerified = true,
            Roles = new(), Groups = new(),
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
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
