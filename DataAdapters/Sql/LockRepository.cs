using Npgsql;

namespace Dmart.DataAdapters.Sql;

// locks table — Unique base only (no Metas). Locks auto-expire after
// settings.LockPeriod seconds via a timestamp comparison at read time — we
// don't run a background sweeper, the expiry check is inline on every op.
public sealed class LockRepository(Db db)
{
    // Tries to acquire an exclusive lock. If an existing row is older than
    // `lockPeriodSeconds`, it's evicted as part of the INSERT so the caller
    // gets the lock. Returns true if the caller now holds the lock.
    public async Task<bool> TryLockAsync(
        string spaceName, string subpath, string shortname, string ownerShortname,
        int lockPeriodSeconds, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // Step 1: purge any stale lock for this (space, subpath, shortname).
        await using (var purge = new NpgsqlCommand("""
            DELETE FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3
              AND timestamp < NOW() - ($4 || ' seconds')::interval
            """, conn))
        {
            purge.Parameters.Add(new() { Value = shortname });
            purge.Parameters.Add(new() { Value = spaceName });
            purge.Parameters.Add(new() { Value = subpath });
            purge.Parameters.Add(new() { Value = lockPeriodSeconds.ToString() });
            await purge.ExecuteNonQueryAsync(ct);
        }
        // Step 2: insert — succeeds only if no live lock is left.
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO locks (uuid, shortname, space_name, subpath, owner_shortname, timestamp)
            VALUES (gen_random_uuid(), $1, $2, $3, $4, NOW())
            ON CONFLICT (shortname, space_name, subpath) DO NOTHING
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = ownerShortname });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> UnlockAsync(string spaceName, string subpath, string shortname, string ownerShortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3 AND owner_shortname = $4
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = ownerShortname });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // Returns the owner if there's a non-expired lock; null if there's no
    // lock OR the existing row is past its lock_period.
    public async Task<string?> GetLockerAsync(
        string spaceName, string subpath, string shortname,
        int lockPeriodSeconds, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT owner_shortname FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3
              AND timestamp >= NOW() - ($4 || ' seconds')::interval
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = lockPeriodSeconds.ToString() });
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
