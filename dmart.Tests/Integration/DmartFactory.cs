using Dmart.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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

    public string AdminShortname { get; } =
        Environment.GetEnvironmentVariable("DMART_TEST_ADMIN")
        ?? ReadFromConfigEnv("ADMIN_SHORTNAME")
        ?? "testadmin";

    public string AdminPassword { get; } =
        Environment.GetEnvironmentVariable("DMART_TEST_PWD")
        ?? ReadFromConfigEnv("ADMIN_PASSWORD")
        ?? "testpassword12345";

    private static string? ReadFromConfigEnv(string key)
    {
        var configFile = DotEnv.FindConfigFile();
        if (configFile is null) return null;
        var raw = DotEnv.Parse(configFile);
        return raw.GetValueOrDefault(key);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Suppress Program.cs's DotEnv.Load() so the test overrides are
        // authoritative. /dev/null exists on Linux but has no content.
        Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Use the resolved connection string (env var or config.env).
                ["Dmart:PostgresConnection"] = PgConn,
                // Null out individual components so Db.BuildConnectionString
                // doesn't assemble from stale/partial dotenv leftovers.
                ["Dmart:DatabaseHost"] = null,
                ["Dmart:DatabasePassword"] = null,
                ["Dmart:DatabaseName"] = null,
                ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes",
                ["Dmart:JwtIssuer"] = "dmart",
                ["Dmart:JwtAudience"] = "dmart",
                ["Dmart:JwtAccessMinutes"] = "5",
                ["Dmart:AdminShortname"] = AdminShortname,
                ["Dmart:AdminPassword"] = AdminPassword,
                ["Dmart:AdminEmail"] = "admin@test.local",
            });
        });
    }
}
