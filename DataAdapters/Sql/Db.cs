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

    // Opens a fast-import session: a dedicated connection in
    // session_replication_role='replica' (bypasses FK constraints AND
    // user-defined triggers for every statement on the session) with an open
    // transaction. Used by `dmart import --fast` to turn the source into a
    // trusted bulk load.
    //
    // Unlike a single import-long transaction, the session commits at BATCH
    // boundaries via RunBatchAsync, so a transport drop costs only the
    // in-flight batch — and the session transparently reconnects and replays
    // the batch (the bulk merge is idempotent under ON CONFLICT, and prior
    // batches are already committed). This is what lets a multi-hour import
    // survive a firewall/NAT idle reset or a briefly-restarted server instead
    // of discarding hours of work. The SET is session-level (not LOCAL), so it
    // persists across the per-batch commits and is re-applied after a reconnect.
    //
    // Hard-fails with a clear InvalidOperationException if the caller's DB
    // role can't set session_replication_role (requires superuser OR the
    // pg_session_replication_role predefined role since PG 14). No silent
    // fallback — the operator explicitly opted into `--fast`.
    public async Task<FastImportSession> BeginFastImportSessionAsync(CancellationToken ct = default)
    {
        var session = new FastImportSession(this, bypassConstraints: true);
        await session.OpenAsync(ct);
        return session;
    }

    // Same reconnectable, batch-committing session WITHOUT the replica role —
    // FK constraints and triggers stay enforced. This is the default import
    // path: it gets the bulk COPY + merge batching, while integrity errors
    // still surface (and are isolated per row by the importer's fallback).
    public async Task<FastImportSession> BeginBatchImportSessionAsync(CancellationToken ct = default)
    {
        var session = new FastImportSession(this, bypassConstraints: false);
        await session.OpenAsync(ct);
        return session;
    }

    // A reconnectable, batch-committing import session. The import calls
    // RunBatchAsync once per bulk batch (durable + retryable), then MarkSuccess()
    // before disposing so the trailing transaction (e.g. the non-idempotent
    // history slice, which deliberately runs outside the per-batch path) is
    // committed. Per-row failures the import collects into `results.Failed` are
    // NOT throws and don't prevent the commit.
    public sealed class FastImportSession : IAsyncDisposable
    {
        private readonly Db _db;
        private readonly bool _bypassConstraints;
        private NpgsqlConnection _conn = null!;
        private NpgsqlTransaction _tx = null!;
        private bool _success;

        internal FastImportSession(Db db, bool bypassConstraints)
        {
            _db = db;
            _bypassConstraints = bypassConstraints;
        }

        // True when the session runs in replica role (--fast): FK constraints
        // and triggers are bypassed, so integrity errors can't occur and the
        // importer's per-row fallback must not engage (its autonomous per-row
        // connections WOULD enforce FKs, silently changing --fast semantics).
        public bool BypassConstraints => _bypassConstraints;

        // The live connection. NOT stable across a reconnect — callers issuing
        // DB I/O must read this at the point of use, never cache it, or a
        // mid-run reconnect hands them a disposed connection.
        public NpgsqlConnection Connection => _conn;

        internal async Task OpenAsync(CancellationToken ct)
        {
            _conn = await _db.OpenAsync(ct);
            try
            {
                if (_bypassConstraints) await SetReplicaRoleAsync(_conn, ct);
                // Npgsql auto-associates the open transaction with any
                // NpgsqlCommand built on _conn, so the repos and bulk helpers
                // don't need to know about it.
                _tx = await _conn.BeginTransactionAsync(ct);
            }
            catch (PostgresException ex)
            {
                await _conn.DisposeAsync();
                throw new InvalidOperationException(
                    "dmart import --fast requires the DB role to be superuser or hold "
                    + $"pg_session_replication_role; SET session_replication_role failed: {ex.MessageText}",
                    ex);
            }
            catch
            {
                await _conn.DisposeAsync();
                throw;
            }
        }

        public void MarkSuccess() => _success = true;

        // Run one batch as its own durable unit: execute `body` in the current
        // transaction, COMMIT it, then open a fresh transaction for the next
        // batch. On a TRANSPORT-level failure (connection reset, socket/IO
        // error — NOT a PostgresException, which is a deterministic server-side
        // rejection that replay won't fix) the dead connection is replaced and
        // `body` is replayed. Safe because prior batches are already committed
        // and the bulk merge is idempotent (ON CONFLICT). `body` returns its
        // stats deltas; the caller applies them only AFTER this returns, so a
        // replay can't double-count.
        public async Task<T> RunBatchAsync<T>(
            Func<NpgsqlConnection, CancellationToken, Task<T>> body,
            ILogger? log, CancellationToken ct)
        {
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await body(_conn, ct);
                    await _tx.CommitAsync(ct);
                    await _tx.DisposeAsync();
                    _tx = await _conn.BeginTransactionAsync(ct);
                    return result;
                }
                // Retry on either an explicitly transient exception OR a dead
                // connection (State != Open). The latter catches the case where
                // a prior reconnect's connection later broke and Npgsql surfaces
                // it as `InvalidOperationException("Connection is not open")` on
                // the next command — IsTransient doesn't recognize that flavor,
                // but the state check is authoritative: if the connection isn't
                // open, the only path forward is a fresh one + replay.
                catch (Exception ex) when (attempt < maxAttempts
                                           && (IsTransient(ex) || _conn.State != System.Data.ConnectionState.Open)
                                           && !ct.IsCancellationRequested)
                {
                    log?.LogWarning(ex,
                        "import: transport error on batch (attempt {Attempt}/{Max}, conn state {State}); reconnecting",
                        attempt, maxAttempts, _conn.State);
                    // Exponential backoff capped at 5s — a NAT reaper or a
                    // restarting server may need a beat before a fresh
                    // connection is accepted.
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 200 * (1 << (attempt - 1)))), ct);
                    await ReconnectAsync(ct);
                }
            }
        }

        // Replace a dead connection with a fresh one in replica mode + open tx.
        // The old tx/conn are disposed best-effort — they're already broken.
        private async Task ReconnectAsync(CancellationToken ct)
        {
            try { await _tx.DisposeAsync(); }   catch { /* already broken */ }
            try { await _conn.DisposeAsync(); } catch { /* already broken */ }
            _conn = await _db.OpenAsync(ct);
            if (_bypassConstraints) await SetReplicaRoleAsync(_conn, ct);
            _tx = await _conn.BeginTransactionAsync(ct);
        }

        // Recover from a deterministically-aborted transaction (e.g. an
        // integrity violation surfaced by the batch merge or its deferred-FK
        // commit): roll the dead transaction back and open a fresh one so the
        // session can keep batching. The connection itself is still healthy.
        public async Task ResetTransactionAsync(CancellationToken ct)
        {
            try { await _tx.RollbackAsync(ct); } catch { /* already aborted server-side */ }
            await _tx.DisposeAsync();
            _tx = await _conn.BeginTransactionAsync(ct);
        }

        // Run one statement inside a savepoint so a per-row/per-line failure
        // (e.g. one bad history line) aborts only the savepoint, not the
        // session's open transaction. Without this, the first bad line poisons
        // the transaction and every subsequent statement fails with 25P02.
        public async Task RunInSavepointAsync(
            Func<NpgsqlConnection, CancellationToken, Task> body, CancellationToken ct)
        {
            await _tx.SaveAsync("sp_import_row", ct);
            try
            {
                await body(_conn, ct);
                await _tx.ReleaseAsync("sp_import_row", ct);
            }
            catch
            {
                await _tx.RollbackAsync("sp_import_row", ct);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_success) await _tx.CommitAsync();
                else          await _tx.RollbackAsync();
            }
            catch
            {
                // Best-effort: a broken connection means the transaction is
                // already aborted server-side. Continue to restore the role.
            }
            finally
            {
                await _tx.DisposeAsync();
            }
            if (_bypassConstraints)
            {
                try
                {
                    await using var cmd = new NpgsqlCommand("SET session_replication_role = DEFAULT", _conn);
                    await cmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Same reasoning as above.
                }
            }
            await _conn.DisposeAsync();
        }

        private static async Task SetReplicaRoleAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("SET session_replication_role = 'replica'", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Is this a lost/unusable connection (reconnect + replay is right) or a
        // deterministic server rejection (replay won't help — let it abort)?
        // A dropped connection reaches us two ways: as a raw transport error
        // (NpgsqlException "Exception while reading from stream" / socket / IO),
        // OR as a PostgresException whose SQLSTATE is a connection-failure class
        // — e.g. 57P01 when a firewall/idle reaper or `pg_terminate_backend`
        // tears down the backend, or a class-08 connection exception. Everything
        // else (constraint violations, bad SQL) is deterministic and must NOT
        // retry, or we'd burn the retry budget masking a real error.
        private static bool IsTransient(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is PostgresException pg) return IsConnectionFailureState(pg.SqlState);
                if (e is NpgsqlException or System.Net.Sockets.SocketException or System.IO.IOException or TimeoutException)
                    return true;
            }
            return false;
        }

        private static bool IsConnectionFailureState(string? sqlState) => sqlState switch
        {
            // Class 08 — connection exception.
            "08000" or "08003" or "08006" or "08001" or "08004" or "08007" or "08P01"
            // Class 57 — operator intervention that tears down the connection
            // (admin terminate, crash shutdown, cannot-connect-now).
            or "57P01" or "57P02" or "57P03" => true,
            _ => false,
        };
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
        // Protocol keepalive (an idle-time SELECT 1) keeps NAT/firewall flow
        // state warm and surfaces a dead peer quickly; TcpKeepAlive adds the
        // OS-level SO_KEEPALIVE so a half-open socket during a long COPY is
        // detected too. Both off when DatabaseKeepalive is 0.
        if (s.DatabaseKeepalive > 0)
        {
            csb.KeepAlive = s.DatabaseKeepalive;
            csb.TcpKeepAlive = true;
        }
        return csb.ConnectionString;
    }
}
