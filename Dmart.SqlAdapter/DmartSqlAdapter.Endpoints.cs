using System.Globalization;
using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.SqlAdapter.Helpers;
using Dmart.SqlAdapter.Permissions;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.SqlAdapter;

// DB-feasible extensions to the SqlAdapter surface. These mirror dmart server
// features that DON'T require plugin dispatch, schema validation, workflow
// transitions, or external services — they're just table reads/writes.
//
// In-scope here:
//   - History row reads + writes (audit trail).
//   - Locks (acquire / release / inspect).
//   - Schema entry lookup (a thin alias around LoadAsync(ResourceType.Schema)).
//
// Out-of-scope (Client-only — these need the dmart server):
//   - Plugin dispatch, schema validation, workflow transitions.
//   - Embeddings/semantic search, OAuth, WebSocket, MCP, QR.
//   - Import/export orchestration, short-link generation.
//   - Manifest/settings, security cache reload.
public sealed partial class DmartSqlAdapter
{
    // -------------------------------------------------------------------
    // History
    //
    // Mirrors HistoryRepository / QueryService(type=history) on the server.
    // The record shape is the dmart `histories` row, projected as a typed
    // record so consumers don't have to reinvent the column list.
    // -------------------------------------------------------------------

    public sealed record HistoryRow
    {
        public required string Uuid { get; init; }
        public required string SpaceName { get; init; }
        public required string Subpath { get; init; }
        public required string Shortname { get; init; }
        public DateTime Timestamp { get; init; }
        public string? OwnerShortname { get; init; }
        public Dictionary<string, object>? RequestHeaders { get; init; }
        public Dictionary<string, object>? Diff { get; init; }
        public string? LastChecksumHistory { get; init; }
    }

    // List history rows for a (space, subpath, shortname) target. Newest
    // first. RBAC: the histories table is excluded from the per-row ACL
    // filter on the server (PermissionFilter.Append checks tableName
    // against a skip list) — we match that here: read access is governed
    // by the caller's `view` permission on the parent entry, NOT by the
    // history row itself.
    public async Task<List<HistoryRow>> QueryHistoryAsync(
        string spaceName, string subpath, string shortname,
        int limit = 50, string? actor = null, CancellationToken ct = default)
    {
        subpath = Locator.NormalizeSubpath(subpath);

        // Permission gate: require `view` on the underlying entry. The
        // history rows themselves aren't ACL'd, but you shouldn't be able
        // to read the audit trail of an entry you can't see.
        if (_engine is not null)
        {
            var existing = await LoadRawAsync(spaceName, subpath, shortname, ct).ConfigureAwait(false);
            if (existing is null) return new();
            await _engine.RequireAsync(actor,
                "view",
                new Locator(existing.ResourceType, spaceName, subpath, shortname),
                ResourceContext.FromEntry(existing), null, ct).ConfigureAwait(false);
        }

        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT uuid, space_name, subpath, shortname, timestamp,
                   owner_shortname, request_headers, diff, last_checksum_history
              FROM histories
             WHERE space_name = $1 AND subpath = $2 AND shortname = $3
             ORDER BY timestamp DESC
             LIMIT $4
            """, conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = limit });

        var rows = new List<HistoryRow>(limit);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new HistoryRow
            {
                Uuid = r.GetGuid(0).ToString(),
                SpaceName = r.GetString(1),
                Subpath = r.GetString(2),
                Shortname = r.GetString(3),
                Timestamp = r.GetDateTime(4),
                OwnerShortname = r.IsDBNull(5) ? null : r.GetString(5),
                RequestHeaders = r.ReadJsonb<Dictionary<string, object>>(6, _json),
                Diff = r.ReadJsonb<Dictionary<string, object>>(7, _json),
                LastChecksumHistory = r.IsDBNull(8) ? null : r.GetString(8),
            });
        }
        return rows;
    }

    // Append a single history row. Mirrors HistoryRepository.AppendAsync on
    // the server. Headers / diff are stored as JSONB (default "{}" when null,
    // matching the server's NOT NULL contract on those columns).
    public async Task AppendHistoryAsync(
        string spaceName, string subpath, string shortname,
        string? actor,
        Dictionary<string, object>? requestHeaders,
        Dictionary<string, object>? diff,
        CancellationToken ct = default)
    {
        subpath = Locator.NormalizeSubpath(subpath);

        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO histories (uuid, request_headers, diff, timestamp,
                                   owner_shortname, last_checksum_history,
                                   space_name, subpath, shortname)
            VALUES (gen_random_uuid(), $1, $2, NOW(), $3, NULL, $4, $5, $6)
            """, conn);
        cmd.Parameters.Add(new()
        {
            Value = requestHeaders is null
                ? "{}"
                : JsonSerializer.Serialize(requestHeaders, _json),
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.Add(new()
        {
            Value = diff is null ? "{}" : JsonSerializer.Serialize(diff, _json),
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.Add(new() { Value = (object?)actor ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Locks
    //
    // Mirrors LockRepository on the server. Locks auto-expire after the
    // adapter-wide DmartSqlAdapterOptions.LockPeriodSeconds (default 300s,
    // matches dmart's settings.LockPeriod). Expired rows are purged inline
    // as part of the TryLock INSERT.
    //
    // The per-call `lockPeriodSeconds` parameter is kept for signature
    // parity with dmart.Client's LockEntry/UnlockEntry shape but is NOT
    // honored: a caller-controlled period would let one caller's short TTL
    // purge another caller's still-valid long lock. Set the period via
    // DmartSqlAdapterOptions.LockPeriodSeconds at construction time.
    // -------------------------------------------------------------------

    /// <summary>
    /// Acquire an exclusive lock. Returns true if the caller now holds it.
    /// The <paramref name="lockPeriodSeconds"/> argument is ignored — the
    /// adapter-wide period from <see cref="DmartSqlAdapterOptions.LockPeriodSeconds"/>
    /// is used so concurrent callers agree on the staleness threshold.
    /// </summary>
    public async Task<bool> TryLockAsync(
        Locator locator, string ownerShortname, int lockPeriodSeconds = 300,
        CancellationToken ct = default)
    {
        _ = lockPeriodSeconds;  // Parity-only; see method docs.
        var subpath = Locator.NormalizeSubpath(locator.Subpath);

        // Permission: lock is a state-change on the entry — require `update`.
        if (_engine is not null)
        {
            var existing = await LoadRawAsync(locator.SpaceName, subpath, locator.Shortname, ct).ConfigureAwait(false);
            if (existing is null) return false;
            await _engine.RequireAsync(ownerShortname, "update", locator,
                ResourceContext.FromEntry(existing), null, ct).ConfigureAwait(false);
        }

        var period = _options.LockPeriodSeconds;
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        // Two-step: purge stale, then insert. Same pattern as server's
        // LockRepository.TryLockAsync — keeps the row count bounded and
        // lets us reuse the UNIQUE constraint as the gate.
        await using (var purge = new NpgsqlCommand("""
            DELETE FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3
              AND timestamp < NOW() - ($4 || ' seconds')::interval
            """, conn))
        {
            purge.Parameters.Add(new() { Value = locator.Shortname });
            purge.Parameters.Add(new() { Value = locator.SpaceName });
            purge.Parameters.Add(new() { Value = subpath });
            purge.Parameters.Add(new() { Value = period.ToString(CultureInfo.InvariantCulture) });
            await purge.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO locks (uuid, shortname, space_name, subpath, owner_shortname, timestamp)
            VALUES (gen_random_uuid(), $1, $2, $3, $4, NOW())
            ON CONFLICT (shortname, space_name, subpath) DO NOTHING
            """, conn);
        cmd.Parameters.Add(new() { Value = locator.Shortname });
        cmd.Parameters.Add(new() { Value = locator.SpaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = ownerShortname });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    // Release a lock held by `ownerShortname`. Returns true if a row was
    // actually deleted (false on a no-op release).
    public async Task<bool> UnlockAsync(Locator locator, string ownerShortname,
        CancellationToken ct = default)
    {
        var subpath = Locator.NormalizeSubpath(locator.Subpath);
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3
              AND owner_shortname = $4
            """, conn);
        cmd.Parameters.Add(new() { Value = locator.Shortname });
        cmd.Parameters.Add(new() { Value = locator.SpaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = ownerShortname });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Returns the current lock holder, or null when no live lock exists.
    /// The <paramref name="lockPeriodSeconds"/> argument is ignored — the
    /// adapter-wide period from <see cref="DmartSqlAdapterOptions.LockPeriodSeconds"/>
    /// is used so all callers agree on staleness.
    /// </summary>
    public async Task<string?> GetLockerAsync(Locator locator,
        int lockPeriodSeconds = 300, CancellationToken ct = default)
    {
        _ = lockPeriodSeconds;  // Parity-only; see method docs.
        var subpath = Locator.NormalizeSubpath(locator.Subpath);
        var period = _options.LockPeriodSeconds;
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT owner_shortname FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3
              AND timestamp >= NOW() - ($4 || ' seconds')::interval
            """, conn);
        cmd.Parameters.Add(new() { Value = locator.Shortname });
        cmd.Parameters.Add(new() { Value = locator.SpaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = period.ToString(CultureInfo.InvariantCulture) });
        return (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Schema entries
    //
    // dmart schemas live in the `entries` table with resource_type='schema'.
    // This thin wrapper is here purely for parity with the Python adapter's
    // surface; consumers could call LoadAsync(..., ResourceType.Schema, ...)
    // directly and get the same result.
    // -------------------------------------------------------------------

    public Task<Entry?> GetSchemaAsync(string spaceName, string shortname,
        string? actor = null, CancellationToken ct = default)
        => LoadAsync(spaceName, "/schema", shortname, ResourceType.Schema, actor, ct);
}
