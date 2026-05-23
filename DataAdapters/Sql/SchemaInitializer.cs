using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Runs once on startup; creates tables if they don't exist. Idempotent.
public sealed class SchemaInitializer(Db db, ILogger<SchemaInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (!db.IsConfigured) return;

        // Use a PostgreSQL advisory lock so parallel test hosts (xUnit creates
        // multiple WebApplicationFactory instances) don't deadlock on concurrent
        // CREATE TABLE / ALTER TABLE statements against the same tables.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var conn = await db.OpenAsync(ct);
                // Advisory lock 1 — serializes schema init across connections.
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_lock(1)", conn))
                    await lk.ExecuteNonQueryAsync(ct);
                try
                {
                    await using var cmd = new NpgsqlCommand(SqlSchema.CreateAll, conn);
                    // No client-side timeout for schema init. The TIMESTAMPTZ→TIMESTAMP
                    // migration rewrites every row in large tables (entries, histories),
                    // which exceeds Npgsql's default 30s. A timeout here aborts the
                    // rewrite mid-flight; SchemaInitializer's catch-all then logs and
                    // continues without surfacing the failure to the operator.
                    cmd.CommandTimeout = 0;
                    await cmd.ExecuteNonQueryAsync(ct);
                    log.LogInformation("dmart schema ready");
                    // If the `hstore` extension was just created by CreateAll,
                    // connections opened BEFORE this point cached the server's
                    // type map without hstore and will throw
                    // "data type name 'hstore' could not be found" when
                    // OtpRepository tries to bind a parameter. Reload the
                    // current connection's types and clear the pool so every
                    // subsequent connection re-fetches the type map.
                    await conn.ReloadTypesAsync(ct);

                    // Concurrent index builds run as separate commands so
                    // they're not wrapped in the implicit transaction that
                    // Postgres applies to multi-statement simple queries.
                    // CREATE INDEX CONCURRENTLY hard-errors inside any tx.
                    // IF NOT EXISTS keeps this idempotent — fresh installs
                    // build the index in milliseconds (empty table); upgrades
                    // against an existing entries table build it in the
                    // background without blocking writes. The advisory lock
                    // serializes concurrent startup attempts, but the build
                    // itself isn't lock-holding once it's in flight.
                    foreach (var sql in SqlSchema.ConcurrentIndexes)
                    {
                        try
                        {
                            await using var icmd = new NpgsqlCommand(sql, conn);
                            icmd.CommandTimeout = 0;
                            await icmd.ExecuteNonQueryAsync(ct);
                            log.LogInformation("concurrent index ready: {Sql}", sql);
                        }
                        catch (Npgsql.PostgresException ex)
                        {
                            // The CONCURRENTLY build can fail (cancelled,
                            // disk full, etc.) leaving an INVALID index
                            // that IF NOT EXISTS won't replace. We log
                            // loudly so the operator knows to manually
                            // DROP INDEX and restart; the system stays
                            // functional in the meantime, just without
                            // the trigram acceleration for wildcard
                            // searches (they fall back to seq scan).
                            log.LogWarning(ex,
                                "concurrent index build failed: {Sql} — wildcard " +
                                "searches will seq-scan until this is resolved; " +
                                "manual recovery: DROP INDEX (the invalid one) and " +
                                "restart dmart", sql);
                        }
                    }
                }
                finally
                {
                    await using var ul = new NpgsqlCommand("SELECT pg_advisory_unlock(1)", conn);
                    await ul.ExecuteNonQueryAsync(ct);
                }
                NpgsqlConnection.ClearAllPools();
                return; // success
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "40P01") // deadlock
            {
                if (attempt == 2) log.LogWarning("schema init deadlock after 3 attempts — continuing");
                else await Task.Delay(200 * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "schema initialization failed — continuing without DB");
                return;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
