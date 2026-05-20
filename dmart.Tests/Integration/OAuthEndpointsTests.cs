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
    public async Task Resolver_CreatesNewUser_WithAutoShortname_AndPersistsProviderId()
    {
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();

        var providerId = Guid.NewGuid().ToString("N")[..12];
        var info = new OAuthUserInfo(
            Provider: "google", ProviderId: providerId,
            Email: $"{providerId}@test.local",
            FirstName: "Test", LastName: "Ninja", PictureUrl: "https://x/pic.jpg");

        User? created = null;
        try
        {
            created = await resolver.ResolveAsync(info);
            // Shortname follows the auto convention: 8 lowercase hex chars
            // sourced from a fresh UUID — no provider prefix.
            created.Shortname.Length.ShouldBe(8);
            created.Shortname.ShouldMatch("^[0-9a-f]{8}$");
            created.IsActive.ShouldBeTrue();
            created.IsEmailVerified.ShouldBeTrue();
            created.GoogleId.ShouldBe(providerId);
            created.Displayname!.En.ShouldBe("Test Ninja");
            created.SocialAvatarUrl.ShouldBe("https://x/pic.jpg");

            // Second call returns the same row — lookup is now by google_id,
            // not by the (now random) shortname.
            var again = await resolver.ResolveAsync(info);
            again.Uuid.ShouldBe(created.Uuid);
            again.Shortname.ShouldBe(created.Shortname);
        }
        finally
        {
            if (created is not null)
                try { await users.DeleteAsync(created.Shortname); } catch { }
        }
    }

    // Apple Sign-in counterpart to the google resolver test. Pins that the
    // Provider="apple" branch populates the dedicated AppleId column rather
    // than falling back to Notes (the pre-v0.9.13 behaviour the model
    // comment used to describe).
    [FactIfPg]
    public async Task Resolver_CreatesNewUser_WithAppleProvider_PopulatesAppleId()
    {
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();

        var providerId = Guid.NewGuid().ToString("N")[..12];
        var info = new OAuthUserInfo(
            Provider: "apple", ProviderId: providerId,
            Email: $"{providerId}@test.local",
            FirstName: "Apple", LastName: "User", PictureUrl: null);

        User? created = null;
        try
        {
            created = await resolver.ResolveAsync(info);
            // Auto shortname — see Resolver_CreatesNewUser_WithAutoShortname.
            created.Shortname.Length.ShouldBe(8);
            created.AppleId.ShouldBe(providerId, "AppleId column must be populated for provider=apple");
            // Sibling provider IDs stay null — no cross-provider bleed.
            created.GoogleId.ShouldBeNull();
            created.FacebookId.ShouldBeNull();
        }
        finally
        {
            if (created is not null)
                try { await users.DeleteAsync(created.Shortname); } catch { }
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
        // tables. The new filter shape mirrors permissions: subpaths is a
        // dict keyed by space (or __all_spaces__) so the plugin self-declares
        // scope. Concurrent=false on the after wrapper so the count is
        // observable synchronously — the default fire-and-forget branch would
        // race the assert.
        var filter = new EventFilter
        {
            Subpaths = new() { ["__all_spaces__"] = new() { "users" } },
            ResourceTypes = new() { "user" },
            // SchemaShortnames left empty = match every schema (mirrors permissions).
            Actions = new() { "create" },
        };
        plugins.Register(new[]
        {
            new PluginWrapper
            {
                Shortname = beforePlugin.Shortname,
                IsActive = true,
                Filters = filter,
                ListenTime = EventListenTime.Before,
                Type = PluginType.Hook,
            },
            new PluginWrapper
            {
                Shortname = afterPlugin.Shortname,
                IsActive = true,
                Filters = filter,
                ListenTime = EventListenTime.After,
                Type = PluginType.Hook,
                Concurrent = false,
            },
        });

        var providerId = Guid.NewGuid().ToString("N")[..12];
        var info = new OAuthUserInfo("google", providerId, $"{providerId}@test.local",
            "Hook", "Tester", null);

        User? created = null;
        try
        {
            // First login → user doesn't exist yet → both hooks fire.
            created = await resolver.ResolveAsync(info);
            // Second login → same provider id → existing-by-google_id → only before fires.
            await resolver.ResolveAsync(info);

            beforePlugin.CountFor(created.Shortname).ShouldBe(2,
                "before-hook is fired unconditionally at the top of ResolveAsync (Python parity)");
            afterPlugin.CountFor(created.Shortname).ShouldBe(1,
                "after-hook fires only on the actual create branch (Python parity)");
        }
        finally
        {
            if (created is not null)
                try { await users.DeleteAsync(created.Shortname); } catch { }
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
    public async Task Resolver_EmailCollidesWithLocalUser_RejectsCreate_NoSilentMerge()
    {
        // Defense-in-depth: a previous version of the resolver would attach
        // the OAuth provider id to ANY local user with a matching email, a
        // silent pre-auth account-takeover primitive. The resolver has since
        // been hardened to never merge on email, and the email column itself
        // is now UNIQUE at the DB layer (SqlSchema.cs:434). The two together
        // mean a colliding-email OAuth create fails loudly instead of
        // silently usurping the victim's row.
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
        try
        {
            var info = new OAuthUserInfo("google", providerId, email, "A", "B", null);
            var ex = await Should.ThrowAsync<Npgsql.PostgresException>(
                () => resolver.ResolveAsync(info));
            ex.SqlState.ShouldBe("23505", "unique_violation on the email index");

            // Pre-existing local user is untouched — no google_id attached.
            var preExisting = await users.GetByShortnameAsync(shortname);
            preExisting.ShouldNotBeNull();
            preExisting!.GoogleId.ShouldBeNull();
            preExisting.Email.ShouldBe(email);
        }
        finally
        {
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }
}
