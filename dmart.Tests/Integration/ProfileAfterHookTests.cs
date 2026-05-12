using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Asserts that POST /user/profile fires the plugin after-hook pipeline by
// observing the side-effect that PluginManager.AfterActionAsync writes
// unconditionally before plugin dispatch: a JSONL line in the space's
// .dm/events.jsonl audit log (PluginManager.cs:251 → SpaceEventLogger.LogAsync).
//
// Two regressions are guarded here:
//   1. The handler must reach AfterActionAsync at all (wiring).
//   2. The Event.Attributes the handler hands to plugins (and the audit log)
//      must NOT contain credential keys — password / old_password /
//      firebase_token — even when the inbound patch carries them.
//
// We use a dedicated WebApplicationFactory subclass so SpacesFolder can be
// pointed at a per-test temp directory without poisoning the shared
// DmartFactory singleton (which other test classes rely on).
public sealed class ProfileAfterHookTests
{
    [FactIfPg]
    public async Task UpdateProfile_Writes_Audit_Line_With_Redacted_Attributes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dmart-aftertest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var auditPath = Path.Combine(tempDir, "management", ".dm", "events.jsonl");

        using var factory = new ProfileAfterHookFactory(tempDir);
        await DmartFactory.ResetBootstrapAdminStateAsync(factory.Services);

        // Borrow DmartFactory's helper to mint a user against THIS factory's
        // host so the login round-trip + the /user/profile call both target
        // the override host (same DB row, different in-memory server).
        using var dmart = new DmartFactory();
        var user = await dmart.CreateLoggedInUserAsync(host: factory);
        try
        {
            // displayname change is what the audit line should preserve.
            // firebase_token + old_password are present to exercise the
            // sanitization path — neither must appear in the audit attributes.
            // firebase_token is a no-op when no session match (UserService:827),
            // old_password is only consulted inside the password-set branch
            // (UserService:639), so neither alters the test user's state.
            var body = """
                {
                  "attributes": {
                    "displayname": {"en": "after-hook-test"},
                    "firebase_token": "fake-fcm-should-not-leak",
                    "old_password": "fake-old-pw-should-not-leak"
                  }
                }
                """;
            var resp = await user.Client.PostAsync(
                "/user/profile",
                new StringContent(body, Encoding.UTF8, "application/json"));
            resp.StatusCode.ShouldBe(HttpStatusCode.OK,
                $"profile update failed: {await resp.Content.ReadAsStringAsync()}");

            // SpaceEventLogger writes synchronously inside AfterActionAsync,
            // but the test still polls — a future change to make the audit
            // writer async-detached would otherwise silently flake the test.
            string[] lines = Array.Empty<string>();
            await WaitFor.UntilAsync(() =>
            {
                if (File.Exists(auditPath))
                    lines = File.ReadAllLines(auditPath);
                return Task.FromResult(lines.Length > 0);
            }, TimeSpan.FromSeconds(2));
            lines.Length.ShouldBeGreaterThan(0, $"no audit lines at {auditPath}");

            // Find the line for this specific user (other concurrent tests
            // could also be writing to /management/.dm/events.jsonl — except
            // we pointed SpacesFolder at a per-test temp dir, so we're alone).
            JsonElement? updateLine = null;
            foreach (var line in lines)
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.GetProperty("request").GetString() != "update") continue;
                if (root.GetProperty("user_shortname").GetString() != user.Shortname) continue;
                updateLine = root.Clone();
                break;
            }
            updateLine.ShouldNotBeNull(
                $"no update audit line for {user.Shortname} in {lines.Length} lines");

            // Resource locator parity with Python's action_log.
            var resource = updateLine.Value.GetProperty("resource");
            resource.GetProperty("type").GetString().ShouldBe("user");
            resource.GetProperty("subpath").GetString().ShouldBe("/users");
            resource.GetProperty("shortname").GetString().ShouldBe(user.Shortname);

            // Positive: displayname change is preserved.
            var attrs = updateLine.Value.GetProperty("attributes");
            attrs.GetProperty("displayname").GetProperty("en").GetString()
                .ShouldBe("after-hook-test");

            // Negative: credential/session keys MUST be stripped before the
            // event is handed to plugins or the audit writer.
            attrs.TryGetProperty("password", out _).ShouldBeFalse(
                "password leaked into audit attributes — sanitization regression");
            attrs.TryGetProperty("old_password", out _).ShouldBeFalse(
                "old_password leaked into audit attributes — sanitization regression");
            attrs.TryGetProperty("firebase_token", out _).ShouldBeFalse(
                "firebase_token leaked into audit attributes — sanitization regression");
        }
        finally
        {
            await user.Cleanup();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Mirrors DmartFactory's config-override shape but adds Dmart:SpacesFolder
    // so SpaceEventLogger.Enabled is true and the audit JSONL gets written.
    // Standalone factory because SpacesFolder is consumed at singleton
    // construction time — we can't flip it on the shared DmartFactory mid-suite.
    private sealed class ProfileAfterHookFactory : WebApplicationFactory<Program>
    {
        private readonly string _spacesFolder;
        public ProfileAfterHookFactory(string spacesFolder) => _spacesFolder = spacesFolder;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes",
                    ["Dmart:JwtIssuer"] = "dmart",
                    ["Dmart:JwtAudience"] = "dmart",
                    ["Dmart:JwtAccessExpires"] = "300",
                    ["Dmart:AdminPassword"] = "Test1234",
                    ["Dmart:AdminEmail"] = "admin@test.local",
                    ["Dmart:AuthRateLimitPerMinute"] = "1000",
                    ["Dmart:SpacesFolder"] = _spacesFolder,
                };
                if (!string.IsNullOrEmpty(DmartFactory.PgConn))
                {
                    overrides["Dmart:PostgresConnection"] = DmartFactory.PgConn;
                    overrides["Dmart:DatabaseHost"] = null;
                    overrides["Dmart:DatabasePassword"] = null;
                    overrides["Dmart:DatabaseName"] = null;
                }
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
