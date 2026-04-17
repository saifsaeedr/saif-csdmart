using Dmart.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
                ["Dmart:JwtAccessMinutes"] = "5",
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
}
