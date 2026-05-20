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

    // Opens a single connection and sets `session_replication_role = 'replica'`
    // on it — bypasses FK constraints AND user-defined triggers for every
    // statement issued on that session. Used by `dmart import --fast` to
    // turn the import zip into a trusted bulk load: every UpsertAsync issued
    // by the import threads this connection in, so the SET stays in effect
    // for the whole import. The returned `Scope` restores the default and
    // disposes the connection on `await using`, so a mid-import throw still
    // leaves the session reset.
    //
    // Hard-fails with a clear InvalidOperationException if the caller's DB
    // role can't set session_replication_role (requires superuser OR the
    // pg_session_replication_role predefined role since PG 14). No silent
    // fallback — the operator explicitly opted into `--fast` and should hear
    // about it when their role can't honour it.
    public async Task<(NpgsqlConnection Conn, FastImportScope Scope)> BeginFastImportSessionAsync(CancellationToken ct = default)
    {
        var conn = await OpenAsync(ct);
        NpgsqlTransaction? tx = null;
        try
        {
            await using (var cmd = new NpgsqlCommand("SET session_replication_role = 'replica'", conn))
                await cmd.ExecuteNonQueryAsync(ct);
            // One transaction for the whole import — collapses N row-level
            // implicit COMMITs (and their fsyncs) into one final COMMIT.
            // Npgsql auto-associates this transaction with any NpgsqlCommand
            // built on `conn`, so the repos don't need to know about it.
            tx = await conn.BeginTransactionAsync(ct);
        }
        catch (PostgresException ex)
        {
            if (tx is not null) await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw new InvalidOperationException(
                "dmart import --fast requires the DB role to be superuser or hold "
                + $"pg_session_replication_role; SET session_replication_role failed: {ex.MessageText}",
                ex);
        }
        catch
        {
            if (tx is not null) await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw;
        }
        return (conn, new FastImportScope(conn, tx));
    }

    // Scope returned by BeginFastImportSessionAsync. The import calls MarkSuccess()
    // exactly once if the whole 5-pass body finished without an unhandled throw;
    // DisposeAsync commits on success, rolls back otherwise. Per-row failures the
    // import collects into `results.Failed` are NOT throws and should NOT prevent
    // the commit — the operator still gets the partial result they would have
    // gotten without --fast.
    public sealed class FastImportScope(NpgsqlConnection conn, NpgsqlTransaction tx) : IAsyncDisposable
    {
        private bool _success;
        public void MarkSuccess() => _success = true;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_success) await tx.CommitAsync();
                else          await tx.RollbackAsync();
            }
            catch
            {
                // Best-effort: a broken connection at this point means the
                // transaction is already aborted server-side. Continue to
                // restore the session role and dispose.
            }
            finally
            {
                await tx.DisposeAsync();
            }
            try
            {
                await using var cmd = new NpgsqlCommand("SET session_replication_role = DEFAULT", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Same reasoning as the transaction dispose above.
            }
            await conn.DisposeAsync();
        }
    }

    // Runs a transactional operation with bounded retry on PG 40P01 (deadlock
    // detected). Deadlocks are transient — the loser's transaction is fully
    // rolled back, so replay from scratch is correct. Use from repositories
    // that perform a multi-statement transaction where concurrent writers
    // can race on row locks (e.g. SpaceRepository.DeleteAsync vs a late
    // plugin writing into `entries`). Any PostgresException other than
    // 40P01 bubbles up unchanged so real errors aren't masked.
    public async Task<T> ExecuteWithRetryOnDeadlockAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "40P01" && attempt < maxAttempts)
            {
                // Linear backoff: 50ms, 100ms. Deadlocks resolve fast so
                // exponential backoff would just add latency without benefit.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
            }
        }
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
