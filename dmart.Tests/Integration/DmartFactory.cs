using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Dmart.Tests.Integration;

// Boots the dmart app in-process for tests. Reads connection details from env vars
// so CI can point at a real PostgreSQL without code changes.
//
//   DMART_TEST_PG_CONN  — full Npgsql connection string. If unset, integration
//                         tests that touch the DB are skipped.
//   DMART_TEST_ADMIN    — bootstrap admin shortname (default: testadmin)
//   DMART_TEST_PWD      — bootstrap admin password (default: testpassword12345)
public sealed class DmartFactory : WebApplicationFactory<Program>
{
    public static string? PgConn => Environment.GetEnvironmentVariable("DMART_TEST_PG_CONN");
    public static bool HasPg => !string.IsNullOrEmpty(PgConn);

    public string AdminShortname { get; } = Environment.GetEnvironmentVariable("DMART_TEST_ADMIN") ?? "testadmin";
    public string AdminPassword  { get; } = Environment.GetEnvironmentVariable("DMART_TEST_PWD")   ?? "testpassword12345";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Suppress dotenv loading by setting BACKEND_ENV to a nonexistent path
        // BEFORE the host builds. This prevents Program.cs's DotEnv.Load() from
        // picking up ~/.dmart/config.env (which may have stale DB paths like a
        // SQLite path) that would override the test's explicit PostgresConnection.
        Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            // The test's PostgresConnection is authoritative. Also null out the
            // individual DATABASE_* components so Db.BuildConnectionString doesn't
            // assemble a stale connection from dotenv leftovers.
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmart:PostgresConnection"] = PgConn,
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
