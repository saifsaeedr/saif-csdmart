using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Dmart.Auth.OAuth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Integration tests for /user/{google,facebook,apple}/callback and
// /mobile-login. We don't hit real provider APIs — instead we verify:
//   1. Unconfigured provider → clean 401 "not configured" JSON error.
//   2. Missing token → clean 401 "missing `token`" JSON error.
//   3. The OAuthUserResolver (unit-level) creates + finds users as Python
//      expects.
//
// Full token-validation tests require mocking outbound HTTP which is heavy
// for AOT; those paths are exercised by hand during integration with real
// Google/Facebook/Apple accounts.
public sealed class OAuthEndpointsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OAuthEndpointsTests(DmartFactory factory) => _factory = factory;

    [TheoryIfPg]
    [InlineData("google")]
    [InlineData("facebook")]
    [InlineData("apple")]
    public async Task MobileLogin_Unconfigured_ReturnsNotConfigured(string provider)
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            $"/user/{provider}/mobile-login",
            new { token = "irrelevant" });

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("not configured");
    }

    [TheoryIfPg]
    [InlineData("google")]
    [InlineData("facebook")]
    [InlineData("apple")]
    public async Task Callback_MissingCode_ReturnsCleanError(string provider)
    {
        using var client = _factory.CreateClient();

        // `?code=` omitted entirely. With providers unconfigured in this
        // factory, `not configured` is the first error to fire for google/
        // facebook; apple short-circuits on missing code when id_token is
        // also missing. Either way, we want a clean 401 with JSON, not a
        // 500.
        var resp = await client.GetAsync($"/user/{provider}/callback");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("\"status\":\"failed\"");
    }

    // OAuthUserResolver — unit-level, but requires DB → put it here with the
    // other DB-backed integration tests so it uses the shared factory.
    [FactIfPg]
    public async Task Resolver_CreatesNewUser_WithProviderShortname()
    {
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();

        var providerId = Guid.NewGuid().ToString("N")[..12];
        var info = new OAuthUserInfo(
            Provider: "google", ProviderId: providerId,
            Email: $"{providerId}@test.local",
            FirstName: "Test", LastName: "Ninja", PictureUrl: "https://x/pic.jpg");

        var expectedShortname = $"google_{providerId}";
        try
        {
            var created = await resolver.ResolveAsync(info);
            created.Shortname.ShouldBe(expectedShortname);
            created.IsActive.ShouldBeTrue();
            created.IsEmailVerified.ShouldBeTrue();
            created.GoogleId.ShouldBe(providerId);
            created.Displayname!.En.ShouldBe("Test Ninja");
            created.SocialAvatarUrl.ShouldBe("https://x/pic.jpg");

            // Second call returns the same row (shortname hit).
            var again = await resolver.ResolveAsync(info);
            again.Uuid.ShouldBe(created.Uuid);
        }
        finally
        {
            try { await users.DeleteAsync(expectedShortname); } catch { }
        }
    }

    // Pins the Python-parity asymmetric hook behavior in OAuthUserResolver: every
    // login (existing or new user) fires the before-hook; the after-hook fires only
    // on the actual create branch. If a future refactor moves the before-hook below
    // the existence check (turning it into create-only), this test fails.
    [FactIfPg]
    public async Task Resolver_FiresBeforeHookOnEveryLogin_AfterHookOnlyOnFirstLogin()
    {
        // Two separate plugin instances — one registered to the before-dispatch
        // table, the other to the after-dispatch table. Using two shortnames keeps
        // the per-side counts cleanly independent (one IHookPlugin instance
        // registered under both ListenTimes would force fragile phase inference).
        var beforePlugin = new BeforeCountingHookPlugin();
        var afterPlugin = new AfterCountingHookPlugin();

        // Spin up an isolated factory so registering a test plugin doesn't bleed
        // into other tests sharing the class-fixture DmartFactory.
        using var host = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IHookPlugin>(beforePlugin);
                s.AddSingleton<IHookPlugin>(afterPlugin);
            });
        });

        // Force the host to construct so Services / PluginManager exist.
        host.CreateClient();
        var plugins = host.Services.GetRequiredService<PluginManager>();
        var resolver = host.Services.GetRequiredService<OAuthUserResolver>();
        var users = host.Services.GetRequiredService<UserRepository>();

        // Register the two test plugins, one each in the before / after dispatch
        // tables. always_active=true bypasses the management space's active_plugins
        // gate. Concurrent=false on the after wrapper so the count is observable
        // synchronously — the default fire-and-forget branch would race the assert.
        var filter = new EventFilter
        {
            Subpaths = new() { "users", "/users" },
            ResourceTypes = new() { "user" },
            SchemaShortnames = new() { "__ALL__" },
            Actions = new() { "create" },
        };
        plugins.Register(new[]
        {
            new PluginWrapper
            {
                Shortname = beforePlugin.Shortname,
                IsActive = true,
                AlwaysActive = true,
                Filters = filter,
                ListenTime = EventListenTime.Before,
                Type = PluginType.Hook,
            },
            new PluginWrapper
            {
                Shortname = afterPlugin.Shortname,
                IsActive = true,
                AlwaysActive = true,
                Filters = filter,
                ListenTime = EventListenTime.After,
                Type = PluginType.Hook,
                Concurrent = false,
            },
        });

        var providerId = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"google_{providerId}";
        var info = new OAuthUserInfo("google", providerId, $"{providerId}@test.local",
            "Hook", "Tester", null);

        try
        {
            // First login → user doesn't exist yet → both hooks fire.
            await resolver.ResolveAsync(info);
            // Second login → same provider id → existing-by-shortname → only before fires.
            await resolver.ResolveAsync(info);

            beforePlugin.CountFor(shortname).ShouldBe(2,
                "before-hook is fired unconditionally at the top of ResolveAsync (Python parity)");
            afterPlugin.CountFor(shortname).ShouldBe(1,
                "after-hook fires only on the actual create branch (Python parity)");
        }
        finally
        {
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    private sealed class BeforeCountingHookPlugin : IHookPlugin
    {
        public string Shortname => "test_oauth_before_counter";
        private readonly ConcurrentDictionary<string, int> _counts = new();
        public int CountFor(string shortname) => _counts.GetValueOrDefault(shortname);
        public Task HookAsync(Event e, CancellationToken ct = default)
        {
            _counts.AddOrUpdate(e.Shortname ?? "", 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }
    }

    private sealed class AfterCountingHookPlugin : IHookPlugin
    {
        public string Shortname => "test_oauth_after_counter";
        private readonly ConcurrentDictionary<string, int> _counts = new();
        public int CountFor(string shortname) => _counts.GetValueOrDefault(shortname);
        public Task HookAsync(Event e, CancellationToken ct = default)
        {
            _counts.AddOrUpdate(e.Shortname ?? "", 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }
    }

    [FactIfPg]
    public async Task Resolver_EmailMatch_CreatesSeparateAccount_NoSilentMerge()
    {
        // Previously: if a local user already had the email the OAuth provider
        // supplied, the resolver would attach the provider id to that account.
        // That was a silent pre-auth account-takeover primitive — anyone able
        // to register a Google/Facebook account with the victim's email would
        // take over the victim's local dmart account on first OAuth login.
        // Now: email is NOT a merge key. The two accounts stay separate;
        // linking has to be a deliberate, authenticated ceremony.
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();

        // Seed a local user with a known email but no google_id.
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var shortname = $"oauth_pre_{suffix}";
        var email = $"{shortname}@test.local";
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Email = email,
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Roles = [], Groups = [],
        });

        var providerId = Guid.NewGuid().ToString("N")[..12];
        var oauthShortname = $"google_{providerId}";
        try
        {
            var info = new OAuthUserInfo("google", providerId, email, "A", "B", null);
            var resolved = await resolver.ResolveAsync(info);

            // New account — shortname keyed on the provider id.
            resolved.Shortname.ShouldBe(oauthShortname);
            resolved.GoogleId.ShouldBe(providerId);
            resolved.IsEmailVerified.ShouldBeTrue();

            // Pre-existing local user is untouched — no google_id attached.
            var preExisting = await users.GetByShortnameAsync(shortname);
            preExisting.ShouldNotBeNull();
            preExisting!.GoogleId.ShouldBeNull();
        }
        finally
        {
            try { await users.DeleteAsync(shortname); } catch { }
            try { await users.DeleteAsync(oauthShortname); } catch { }
        }
    }
}
