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
//   3. The OAuthUserResolver (unit-level) resolves users — by provider
//      shortname or by email — and never auto-creates accounts.
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
    public async Task Resolver_ShortnameHit_ReturnsExistingUser()
    {
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();

        // OAuth login no longer creates accounts — seed the provider-keyed
        // account first, then verify the resolver finds it by shortname.
        var providerId = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"google_{providerId}";
        var email = $"{providerId}@test.local";
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Email = email,
            IsEmailVerified = true,
            GoogleId = providerId,
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Roles = [], Groups = [],
        });

        var info = new OAuthUserInfo(
            Provider: "google", ProviderId: providerId,
            Email: email,
            FirstName: "Test", LastName: "Ninja", PictureUrl: "https://x/pic.jpg");

        try
        {
            var resolved = await resolver.ResolveAsync(info);
            resolved.ShouldNotBeNull();
            resolved!.Shortname.ShouldBe(shortname);
            resolved.IsActive.ShouldBeTrue();
            resolved.GoogleId.ShouldBe(providerId);
            // MaybeRefreshAsync pulls the provider's picture onto the row.
            resolved.SocialAvatarUrl.ShouldBe("https://x/pic.jpg");

            // Second call returns the same row (shortname hit).
            var again = await resolver.ResolveAsync(info);
            again.ShouldNotBeNull();
            again!.Uuid.ShouldBe(resolved.Uuid);
        }
        finally
        {
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    // Apple Sign-in counterpart to the google resolver test. Pins that the
    // Provider="apple" branch populates the dedicated AppleId column when an
    // OAuth login is linked onto an existing account by email.
    [FactIfPg]
    public async Task Resolver_EmailMatch_AppleProvider_PopulatesAppleId()
    {
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();

        // Seed a local account with the email but no provider ids.
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var shortname = $"oauth_apple_{suffix}";
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
            var info = new OAuthUserInfo(
                Provider: "apple", ProviderId: providerId,
                Email: email,
                FirstName: "Apple", LastName: "User", PictureUrl: null);
            var resolved = await resolver.ResolveAsync(info);

            resolved.ShouldNotBeNull();
            resolved!.Shortname.ShouldBe(shortname, "email match links onto the existing account");
            resolved.AppleId.ShouldBe(providerId, "AppleId column must be populated for provider=apple");
            // Sibling provider IDs stay null — no cross-provider bleed.
            resolved.GoogleId.ShouldBeNull();
            resolved.FacebookId.ShouldBeNull();
        }
        finally
        {
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    // OAuth login no longer creates accounts, so there is no after-hook: the
    // before-hook fires unconditionally at the top of every ResolveAsync, and
    // the after-hook never fires. If a future refactor moves the before-hook
    // below the existence check, this test fails.
    [FactIfPg]
    public async Task Resolver_FiresBeforeHookOnEveryLogin_NeverFiresAfterHook()
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
        var shortname = $"google_{providerId}";
        var info = new OAuthUserInfo("google", providerId, $"{providerId}@test.local",
            "Hook", "Tester", null);

        try
        {
            // Two logins for an account that doesn't exist → both return null.
            await resolver.ResolveAsync(info);
            await resolver.ResolveAsync(info);

            beforePlugin.CountFor(shortname).ShouldBe(2,
                "before-hook is fired unconditionally at the top of every ResolveAsync");
            afterPlugin.CountFor(shortname).ShouldBe(0,
                "OAuth login no longer creates accounts, so the after-hook never fires");
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
    public async Task Resolver_EmailMatch_LinksProviderToExistingAccount()
    {
        // When the provider's verified email matches an existing dmart
        // account, the OAuth login resolves to that account and attaches the
        // provider id — there is no second, provider-keyed account. This
        // trusts the provider's email verification; see OAuthUserResolver's
        // class comment for the takeover caveat.
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();

        // Seed a local user with a known email but no google_id.
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var shortname = $"oauth_pre_{suffix}";
        var email = $"{shortname}@test.local";
        var seededUuid = Guid.NewGuid().ToString();
        await users.UpsertAsync(new User
        {
            Uuid = seededUuid,
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
            var resolved = await resolver.ResolveAsync(info);

            // Resolved to the existing account — not a new provider-keyed one.
            resolved.ShouldNotBeNull();
            resolved!.Shortname.ShouldBe(shortname);
            resolved.Uuid.ShouldBe(seededUuid);
            // Provider id is attached to that account.
            resolved.GoogleId.ShouldBe(providerId);
        }
        finally
        {
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }
}
