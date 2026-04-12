using Dmart.Models.Api;
using Dmart.Models.Enums;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// Shared query-building logic used by every repository's QueryAsync/CountQueryAsync.
// Mirrors the WHERE-clause construction from Python's set_sql_statement_from_query
// (adapter_helpers.py:239-327). Every table in dmart shares the same Metas-base
// columns (space_name, subpath, shortname, resource_type, tags, payload, created_at,
// etc.), so the filter logic is identical; only the FROM clause differs.
//
// The caller provides the table name and gets back SQL + parameters. This avoids
// duplicating the filter_types / filter_shortnames / filter_schema_names /
// filter_tags / search / date-range / sort / limit+offset logic across 5+
// repositories.
public static class QueryHelper
{
    // Builds the WHERE clause fragment (without "WHERE") and populates `args`.
    // Returns the SQL fragment starting from "space_name = $1 ...".
    public static string BuildWhereClause(Query q, List<NpgsqlParameter> args)
    {
        var sql = new System.Text.StringBuilder("space_name = $1 ");
        args.Add(new() { Value = q.SpaceName });

        if (!string.IsNullOrEmpty(q.Subpath) && q.Subpath != "/")
        {
            if (q.ExactSubpath)
            {
                args.Add(new() { Value = q.Subpath });
                sql.Append($"AND subpath = ${args.Count} ");
            }
            else
            {
                args.Add(new() { Value = q.Subpath });
                sql.Append($"AND (subpath = ${args.Count} OR subpath LIKE ${args.Count} || '/%') ");
            }
        }

        if (q.FilterTypes is { Count: > 0 })
        {
            args.Add(new()
            {
                Value = q.FilterTypes.Select(JsonbHelpers.EnumMember).ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            sql.Append($"AND resource_type = ANY(${args.Count}) ");
        }

        if (q.FilterShortnames is { Count: > 0 })
        {
            args.Add(new()
            {
                Value = q.FilterShortnames.ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            sql.Append($"AND shortname = ANY(${args.Count}) ");
        }

        if (q.FilterSchemaNames is { Count: > 0 })
        {
            // Python: "meta" is a sentinel meaning "don't filter". Remove it
            // and only apply the filter if anything remains.
            var effective = q.FilterSchemaNames.Where(n => n != "meta").ToArray();
            if (effective.Length > 0)
            {
                args.Add(new()
                {
                    Value = effective,
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                sql.Append($"AND (payload->>'schema_shortname') = ANY(${args.Count}) ");
            }
        }

        if (q.FilterTags is { Count: > 0 })
        {
            args.Add(new()
            {
                Value = q.FilterTags.ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            sql.Append($"AND tags ?| ${args.Count} ");
        }

        if (!string.IsNullOrEmpty(q.Search))
        {
            args.Add(new() { Value = $"%{q.Search}%" });
            sql.Append($"AND (payload::text ILIKE ${args.Count} OR displayname::text ILIKE ${args.Count} OR description::text ILIKE ${args.Count}) ");
        }

        if (q.FromDate is not null)
        {
            args.Add(new() { Value = q.FromDate.Value });
            sql.Append($"AND created_at >= ${args.Count} ");
        }
        if (q.ToDate is not null)
        {
            args.Add(new() { Value = q.ToDate.Value });
            sql.Append($"AND created_at <= ${args.Count} ");
        }

        return sql.ToString();
    }

    // Appends ORDER BY + LIMIT + OFFSET to a StringBuilder and adds params.
    public static void AppendOrderAndPaging(System.Text.StringBuilder sql, Query q, List<NpgsqlParameter> args)
    {
        if (q.Type == QueryType.Random)
            sql.Append("ORDER BY RANDOM() ");
        else
        {
            sql.Append("ORDER BY updated_at ");
            sql.Append(q.SortType == SortType.Ascending ? "ASC " : "DESC ");
        }

        args.Add(new() { Value = Math.Max(1, q.Limit) });
        sql.Append($"LIMIT ${args.Count} ");
        args.Add(new() { Value = Math.Max(0, q.Offset) });
        sql.Append($"OFFSET ${args.Count}");
    }

    // Helper: run a generic query against a table using a pre-existing SELECT
    // and the shared WHERE/ORDER/LIMIT.
    public static async Task<List<T>> RunQueryAsync<T>(
        Db db, string selectAllColumns, Query q,
        Func<NpgsqlDataReader, T> hydrate,
        CancellationToken ct)
    {
        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args);
        var sql = new System.Text.StringBuilder($"{selectAllColumns} WHERE {where} ");
        AppendOrderAndPaging(sql, q, args);

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            // Python's _set_query_final_results catches per-row conversion
            // errors and skips bad rows (logger.warning). We mirror that so
            // corrupt rows (e.g., invalid resource_type values in the live DB)
            // don't crash the entire query.
            try { results.Add(hydrate(reader)); }
            catch { /* skip row with bad data */ }
        }
        return results;
    }

    public static async Task<int> RunCountAsync(
        Db db, string tableName, Query q,
        CancellationToken ct)
    {
        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args);
        var sql = $"SELECT COUNT(*) FROM {tableName} WHERE {where}";
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }
}
