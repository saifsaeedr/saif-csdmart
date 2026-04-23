using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// histories table — flat (no Metas inheritance in dmart).
public sealed class HistoryRepository(Db db)
{
    public async Task AppendAsync(string spaceName, string subpath, string shortname, string? actor,
                                   Dictionary<string, object>? requestHeaders, Dictionary<string, object>? diff,
                                   CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO histories (uuid, request_headers, diff, timestamp,
                                   owner_shortname, last_checksum_history,
                                   space_name, subpath, shortname)
            VALUES (gen_random_uuid(), $1, $2, NOW(), $3, NULL, $4, $5, $6)
            """, conn);
        // request_headers and diff are NOT NULL in dmart's schema — default to {}.
        cmd.Parameters.Add(new()
        {
            Value = JsonbHelpers.ToJsonb(requestHeaders) ?? "{}",
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.Add(new()
        {
            Value = JsonbHelpers.ToJsonb(diff) ?? "{}",
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.Add(new() { Value = (object?)actor ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<HistoryEntry>> ListAsync(string spaceName, string subpath, string shortname, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT uuid, owner_shortname, diff, timestamp
            FROM histories
            WHERE space_name = $1 AND subpath = $2 AND shortname = $3
            ORDER BY timestamp DESC
            LIMIT $4
            """, conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<HistoryEntry>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new HistoryEntry(
                Uuid: reader.GetGuid(0),
                Actor: reader.IsDBNull(1) ? null : reader.GetString(1),
                Diff: reader.IsDBNull(2) ? null : reader.GetString(2),
                Timestamp: reader.GetDateTime(3)));
        }
        return results;
    }
    // ----- query support (used by QueryService for type=history) -----

    private const string SelectAllColumns = """
        SELECT uuid, request_headers, diff, timestamp, owner_shortname,
               last_checksum_history, space_name, subpath, shortname
        FROM histories
        """;

    public async Task<List<HistoryRecord>> QueryHistoryAsync(Models.Api.Query q, CancellationToken ct = default)
    {
        var args = new List<NpgsqlParameter>();
        var sql = new System.Text.StringBuilder(
            $"{SelectAllColumns} WHERE space_name = $1 ");
        args.Add(new() { Value = q.SpaceName });

        if (!string.IsNullOrEmpty(q.Subpath) && q.Subpath != "/")
        {
            args.Add(new() { Value = q.Subpath });
            sql.Append($"AND (subpath = ${args.Count} OR subpath LIKE ${args.Count} || '/%') ");
        }
        if (q.FilterShortnames is { Count: > 0 })
        {
            args.Add(new() { Value = q.FilterShortnames.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            sql.Append($"AND shortname = ANY(${args.Count}) ");
        }
        if (q.FromDate is not null)
        {
            args.Add(new() { Value = q.FromDate.Value });
            sql.Append($"AND timestamp >= ${args.Count} ");
        }
        if (q.ToDate is not null)
        {
            args.Add(new() { Value = q.ToDate.Value });
            sql.Append($"AND timestamp <= ${args.Count} ");
        }

        sql.Append("ORDER BY timestamp DESC ");
        args.Add(new() { Value = Math.Max(1, q.Limit) });
        sql.Append($"LIMIT ${args.Count} ");
        args.Add(new() { Value = Math.Max(0, q.Offset) });
        sql.Append($"OFFSET ${args.Count}");

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<HistoryRecord>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new HistoryRecord(
                Uuid: reader.GetGuid(0),
                RequestHeaders: reader.IsDBNull(1) ? null : reader.GetString(1),
                Diff: reader.IsDBNull(2) ? null : reader.GetString(2),
                Timestamp: reader.GetDateTime(3),
                OwnerShortname: reader.IsDBNull(4) ? null : reader.GetString(4),
                LastChecksumHistory: reader.IsDBNull(5) ? null : reader.GetString(5),
                SpaceName: reader.GetString(6),
                Subpath: reader.GetString(7),
                Shortname: reader.GetString(8)));
        }
        return results;
    }

    public async Task<int> CountHistoryQueryAsync(Models.Api.Query q, CancellationToken ct = default)
    {
        var args = new List<NpgsqlParameter>();
        var sql = new System.Text.StringBuilder("SELECT COUNT(*) FROM histories WHERE space_name = $1 ");
        args.Add(new() { Value = q.SpaceName });
        if (!string.IsNullOrEmpty(q.Subpath) && q.Subpath != "/")
        {
            args.Add(new() { Value = q.Subpath });
            sql.Append($"AND (subpath = ${args.Count} OR subpath LIKE ${args.Count} || '/%') ");
        }
        if (q.FilterShortnames is { Count: > 0 })
        {
            args.Add(new() { Value = q.FilterShortnames.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            sql.Append($"AND shortname = ANY(${args.Count}) ");
        }
        if (q.FromDate is not null)
        {
            args.Add(new() { Value = q.FromDate.Value });
            sql.Append($"AND timestamp >= ${args.Count} ");
        }
        if (q.ToDate is not null)
        {
            args.Add(new() { Value = q.ToDate.Value });
            sql.Append($"AND timestamp <= ${args.Count} ");
        }
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }
}

public sealed record HistoryEntry(Guid Uuid, string? Actor, string? Diff, DateTime Timestamp);

public sealed record HistoryRecord(
    Guid Uuid, string? RequestHeaders, string? Diff, DateTime Timestamp,
    string? OwnerShortname, string? LastChecksumHistory,
    string SpaceName, string Subpath, string Shortname);
