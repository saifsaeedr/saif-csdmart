using Dmart.Config;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Thin connection factory. Singleton — opens a fresh NpgsqlConnection per call so we
// don't share state across handlers. Npgsql does its own pooling under the hood.
//
// Construction is non-throwing so the host can boot when PostgreSQL isn't configured
// (useful for tests, smoke checks, and graceful degradation). The connection-string
// requirement is enforced at OpenAsync time and surfaces as a clean exception in the
// failing handler rather than a DI resolution failure at startup.
//
// If Dmart:PostgresConnection isn't set explicitly, we assemble it from the
// individual DATABASE_HOST / DATABASE_PORT / DATABASE_USERNAME / DATABASE_PASSWORD /
// DATABASE_NAME / DATABASE_POOL_SIZE components — matching how dmart Python builds
// its SQLAlchemy URL from separate settings. This lets a plain config.env from a
// Python deployment drop in unchanged.
public sealed class Db(IOptions<DmartSettings> settings)
{
    private readonly string? _conn = BuildConnectionString(settings.Value);

    public bool IsConfigured => !string.IsNullOrEmpty(_conn);

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_conn))
            throw new InvalidOperationException("Dmart:PostgresConnection not configured");
        var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        return c;
    }

    // Assemble order: explicit PostgresConnection always wins (lets ops override
    // any individual tuning knob via a raw Npgsql connection string). Otherwise
    // we build from the components. If none of the DATABASE_* fields have been
    // set, we still emit a localhost-ish string with empty password so Npgsql
    // can surface a clearer error than "connection string is null".
    private static string? BuildConnectionString(DmartSettings s)
    {
        if (!string.IsNullOrEmpty(s.PostgresConnection))
            return s.PostgresConnection;

        // Mirror Python's behavior: if every component is still at its default,
        // treat the DB as "not configured" so IsConfigured stays false and
        // smoke/test hosts can boot without Postgres. Null values (from test
        // overrides that explicitly clear a field) are treated as "not set".
        var hasExplicitDbConfig =
            !string.IsNullOrEmpty(s.DatabasePassword)
            || (!string.IsNullOrEmpty(s.DatabaseUsername) && s.DatabaseUsername != "dmart")
            || (!string.IsNullOrEmpty(s.DatabaseName) && s.DatabaseName != "dmart")
            || (!string.IsNullOrEmpty(s.DatabaseHost) && s.DatabaseHost != "localhost")
            || s.DatabasePort != 5432;
        if (!hasExplicitDbConfig) return null;

        // Guard: Host is required by Npgsql — if it's null/empty we can't connect.
        if (string.IsNullOrEmpty(s.DatabaseHost)) return null;

        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = s.DatabaseHost,
            Port = s.DatabasePort,
            Username = s.DatabaseUsername,
            Password = s.DatabasePassword,
            Database = s.DatabaseName,
            MaxPoolSize = s.DatabasePoolSize + s.DatabaseMaxOverflow,
            Timeout = s.DatabasePoolTimeout,
            ConnectionIdleLifetime = s.DatabasePoolRecycle,
        };
        return csb.ConnectionString;
    }
}
