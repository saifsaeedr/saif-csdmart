using System.Net;
using System.Net.Http.Json;
using Dmart.Auth.OAuth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
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
            Subpath = "users",
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
