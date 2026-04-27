using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dmart.Tests.Integration;

// Boots the dmart app in-process for tests.
//
// DB connection resolution (in priority order):
//   1. DMART_TEST_PG_CONN env var (full Npgsql string — for CI overrides)
//   2. config.env in the current working directory (same file devs use to run the binary)
//   3. Not configured → DB-backed tests auto-skip via the HasPg flag
//
// Admin credentials:
//   DMART_TEST_ADMIN / DMART_TEST_PWD env vars, or defaults from config.env.
public sealed class DmartFactory : WebApplicationFactory<Program>
{
    // Resolve connection string: env var → config.env → null.
    private static string? ResolvePgConn()
    {
        // 1. Explicit env var always wins (CI/docker).
        var fromEnv = Environment.GetEnvironmentVariable("DMART_TEST_PG_CONN");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

        // 2. Build from config.env components (same file the binary uses).
        var configFile = DotEnv.FindConfigFile();
        if (configFile is null) return null;
        var raw = DotEnv.Parse(configFile);
        var host = raw.GetValueOrDefault("DATABASE_HOST", "");
        var password = raw.GetValueOrDefault("DATABASE_PASSWORD", "");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(password)) return null;
        var port = raw.GetValueOrDefault("DATABASE_PORT", "5432");
        var user = raw.GetValueOrDefault("DATABASE_USERNAME", "dmart");
        var db = raw.GetValueOrDefault("DATABASE_NAME", "dmart");
        return $"Host={host};Port={port};Username={user};Password={password};Database={db}";
    }

    private static readonly string? _pgConn = ResolvePgConn();
    public static string? PgConn => _pgConn;
    public static bool HasPg => !string.IsNullOrEmpty(PgConn);

    // Cache config.env values in static fields so they are resolved ONCE,
    // before ConfigureWebHost poisons BACKEND_ENV with /dev/null.
    private static readonly Dictionary<string, string> _configEnvCache = LoadConfigEnv();

    // Admin is always "dmart" (hardcoded in AdminBootstrap).
    // Tests set a password via config override so login works.
    public string AdminShortname { get; } = "dmart";
    public string AdminPassword { get; } =
        Environment.GetEnvironmentVariable("DMART_TEST_PWD")
        ?? _configEnvCache.GetValueOrDefault("ADMIN_PASSWORD")
        ?? "testpassword12345";

    private static Dictionary<string, string> LoadConfigEnv()
    {
        var configFile = DotEnv.FindConfigFile();
        return configFile is not null ? DotEnv.Parse(configFile) : new();
    }

    private static string? ReadFromConfigEnv(string key)
    {
        return _configEnvCache.GetValueOrDefault(key);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Suppress Program.cs's DotEnv.Load() so the test overrides are
        // authoritative. /dev/null exists on Linux but has no content.
        Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");

        // Suppress noisy info/warn/fail log output during tests — only show
        // errors that actually cause test failures.
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Error);
        });

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var overrides = new Dictionary<string, string?>
            {
                ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes",
                ["Dmart:JwtIssuer"] = "dmart",
                ["Dmart:JwtAudience"] = "dmart",
                // 300 seconds = 5 minutes — renamed from JwtAccessMinutes,
                // unit is now seconds to match Python's jwt_access_expires.
                ["Dmart:JwtAccessExpires"] = "300",
                ["Dmart:AdminPassword"] = AdminPassword,
                ["Dmart:AdminEmail"] = "admin@test.local",
            };

            // If a PostgresConnection is resolved (from env var or config.env),
            // route the app at it and null out the individual DATABASE_*
            // components so Db.BuildConnectionString doesn't mix the new
            // connection string with stale/partial dotenv leftovers. If PgConn
            // is null, leave the individual components alone so the values
            // loaded from config.env by Program.cs remain authoritative (and
            // the settings validator sees a populated DatabaseHost).
            if (!string.IsNullOrEmpty(PgConn))
            {
                overrides["Dmart:PostgresConnection"] = PgConn;
                overrides["Dmart:DatabaseHost"] = null;
                overrides["Dmart:DatabasePassword"] = null;
                overrides["Dmart:DatabaseName"] = null;
            }

            cfg.AddInMemoryCollection(overrides);
        });
    }

    // Per-test user with a fresh JWT + matching `sessions` row. Use this from
    // any integration test that needs an authenticated client. Each call mints
    // a unique shortname so concurrent xUnit tests don't race on the same
    // admin via MaxSessionsPerUser eviction.
    public sealed record TestUser(
        HttpClient Client, string Token, string Shortname, Func<Task> Cleanup);

    public Task<TestUser> CreateLoggedInUserAsync(
        UserType type = UserType.Web,
        List<string>? roles = null) =>
        CreateLoggedInUserAsync(host: this, type, roles);

    // For tests that drive their own login flow (e.g. /oauth/authorize) and
    // only need the user row to exist, not a pre-issued JWT.
    public sealed record TestCreds(string Shortname, string Password, Func<Task> Cleanup);

    public async Task<TestCreds> CreateTestUserAsync(
        UserType type = UserType.Web,
        List<string>? roles = null)
    {
        var users = Services.GetRequiredService<UserRepository>();
        var hasher = Services.GetRequiredService<PasswordHasher>();
        var shortname = $"itest_{Guid.NewGuid():N}"[..16];
        const string password = "TestPassword1";

        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(password),
            Type = type,
            Language = Language.En,
            Roles = roles ?? new() { "super_admin" },
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        async Task Cleanup()
        {
            try { await users.DeleteAllSessionsAsync(shortname); } catch { }
            try { await users.DeleteAsync(shortname); } catch { }
        }

        return new TestCreds(shortname, password, Cleanup);
    }

    // Override `host` when the test is exercising a factory built via
    // _factory.WithWebHostBuilder(...) so the login round-trip + returned
    // client target the override host (same DB, different in-memory server).
    public async Task<TestUser> CreateLoggedInUserAsync(
        WebApplicationFactory<Program> host,
        UserType type = UserType.Web,
        List<string>? roles = null)
    {
        var users = host.Services.GetRequiredService<UserRepository>();
        var hasher = host.Services.GetRequiredService<PasswordHasher>();
        var shortname = $"itest_{Guid.NewGuid():N}"[..16];
        // Match _factory.AdminPassword so tests that hardcode it for
        // /user/validate_password (and similar self-lookups) still pass.
        var password = AdminPassword;

        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(password),
            // Set email so /user/profile responses include the "email" key
            // (Profile_GET_Returns_All_Python_Parity_Fields asserts it).
            Email = $"{shortname}@test.local",
            IsEmailVerified = true,
            Type = type,
            Language = Language.En,
            Roles = roles ?? new() { "super_admin" },
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var loginClient = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = new UserLoginRequest(shortname, null, null, password, null);
        var loginResp = await loginClient.PostAsJsonAsync(
            "/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await loginResp.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException(
                $"Login failed for '{shortname}': {loginResp.StatusCode} {raw}");

        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        async Task Cleanup()
        {
            try { await users.DeleteAllSessionsAsync(shortname); } catch { }
            try { await users.DeleteAsync(shortname); } catch { }
        }

        return new TestUser(client, token, shortname, Cleanup);
    }
}
