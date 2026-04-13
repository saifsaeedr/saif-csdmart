using System.Text.RegularExpressions;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// Shared query-building logic used by every repository's QueryAsync/CountQueryAsync.
// Mirrors Python's set_sql_statement_from_query + apply_acl_and_query_policies +
// query_aggregation. Every table in dmart shares the same Metas-base columns, so
// the filter logic is identical; only the FROM clause differs.
public static class QueryHelper
{
    // ====================================================================
    // WHERE CLAUSE BUILDER
    // ====================================================================

    public static string BuildWhereClause(Query q, List<NpgsqlParameter> args)
    {
        var sql = new System.Text.StringBuilder("space_name = $1 ");
        args.Add(new() { Value = q.SpaceName });

        // exact_subpath=true: only entries at this exact subpath (including "/").
        // exact_subpath=false + subpath="/": no filter (return all subpaths).
        // exact_subpath=false + subpath!="/": hierarchical match (subpath + children).
        if (q.ExactSubpath)
        {
            args.Add(new() { Value = q.Subpath ?? "/" });
            sql.Append($"AND subpath = ${args.Count} ");
        }
        else if (!string.IsNullOrEmpty(q.Subpath) && q.Subpath != "/")
        {
            args.Add(new() { Value = q.Subpath });
            sql.Append($"AND (subpath = ${args.Count} OR subpath LIKE ${args.Count} || '/%') ");
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

        // RediSearch-style search: @field:value syntax → SQL WHERE clauses.
        if (!string.IsNullOrEmpty(q.Search))
            AppendSearchClauses(sql, q.Search, args);

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

    // ====================================================================
    // REDISEARCH-STYLE SEARCH PARSING
    // ====================================================================
    // Python regex: r'-?@[^:\s]+:"[^"]*"|-?@[^:\s]+:[^\s]+|\S+'
    // Matches: -?@field:"quoted value" | -?@field:unquoted | plain_word
    private static readonly Regex SearchTokenRegex = new(
        @"-?@[^:\s]+:""[^""]*""|-?@[^:\s]+:[^\s]+|\S+",
        RegexOptions.Compiled);

    // Comparison operators at the start of a value: >=, <=, >, <, !
    private static readonly Regex ComparisonRegex = new(
        @"^(>=|<=|>|<|!)(.+)$", RegexOptions.Compiled);

    private static void AppendSearchClauses(System.Text.StringBuilder sql, string search, List<NpgsqlParameter> args)
    {
        var tokens = SearchTokenRegex.Matches(search);
        if (tokens.Count == 0) return;

        var andClauses = new List<string>();

        foreach (Match token in tokens)
        {
            var raw = token.Value;

            // Check for @field:value pattern
            if (raw.Contains('@') && raw.Contains(':'))
            {
                var negate = raw.StartsWith('-');
                if (negate) raw = raw[1..];

                // Split on first ':'
                var atIdx = raw.IndexOf('@');
                var colonIdx = raw.IndexOf(':', atIdx);
                if (colonIdx < 0) continue;

                var field = raw[(atIdx + 1)..colonIdx];
                var value = raw[(colonIdx + 1)..].Trim('"');

                // Handle OR values (pipe-separated: @status:active|pending)
                var orValues = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
                var orClauses = new List<string>();

                foreach (var v in orValues)
                {
                    var clause = BuildFieldClause(field, v.Trim(), args, negate);
                    if (clause is not null) orClauses.Add(clause);
                }

                if (orClauses.Count > 0)
                {
                    var combined = orClauses.Count == 1
                        ? orClauses[0]
                        : $"({string.Join(negate ? " AND " : " OR ", orClauses)})";
                    andClauses.Add(combined);
                }
            }
            else
            {
                // Plain text search — substring match across common text fields.
                // Python's RediSearch indexes all fields; we match by searching
                // shortname + displayname + description + tags + payload text.
                args.Add(new() { Value = $"%{raw}%" });
                andClauses.Add(
                    $"(shortname ILIKE ${args.Count} OR payload::text ILIKE ${args.Count} OR displayname::text ILIKE ${args.Count} OR description::text ILIKE ${args.Count} OR tags::text ILIKE ${args.Count})");
            }
        }

        if (andClauses.Count > 0)
            sql.Append($"AND ({string.Join(" AND ", andClauses)}) ");
    }

    // Builds a single field-level WHERE clause for @field:value.
    // Python maps field paths to JSONB accessors: payload.body.email → payload::jsonb->'body'->>'email'
    private static string? BuildFieldClause(string field, string value, List<NpgsqlParameter> args, bool negate)
    {
        // Check for comparison operator
        string op = "ILIKE";
        var compMatch = ComparisonRegex.Match(value);
        if (compMatch.Success)
        {
            op = compMatch.Groups[1].Value switch
            {
                ">" => ">",
                ">=" => ">=",
                "<" => "<",
                "<=" => "<=",
                "!" => "!=",
                _ => "ILIKE",
            };
            value = compMatch.Groups[2].Value;
        }

        // Build the SQL expression for the field path.
        // Fields starting with "payload.body." are JSONB paths into the payload column.
        // Fields starting with "payload." are JSONB paths.
        // Other fields are direct column references (displayname, shortname, etc.)
        string fieldExpr;
        if (field.StartsWith("payload.body.", StringComparison.Ordinal))
        {
            fieldExpr = BuildJsonbPath("payload", field["payload.".Length..]);
        }
        else if (field.StartsWith("payload.", StringComparison.Ordinal))
        {
            fieldExpr = BuildJsonbPath("payload", field["payload.".Length..]);
        }
        else if (field.Contains('.'))
        {
            // Generic dotted path — assume first segment is the column, rest is JSONB path
            var dot = field.IndexOf('.');
            var col = field[..dot];
            fieldExpr = BuildJsonbPath(col, field[(dot + 1)..]);
        }
        else
        {
            // Direct column — cast to text for ILIKE compatibility
            fieldExpr = $"{field}::text";
        }

        // Build the comparison
        if (op == "ILIKE")
        {
            args.Add(new() { Value = $"%{value}%" });
            return negate
                ? $"({fieldExpr} IS NULL OR {fieldExpr} NOT ILIKE ${args.Count})"
                : $"{fieldExpr} ILIKE ${args.Count}";
        }
        else
        {
            // Numeric/date comparison
            args.Add(new() { Value = value });
            var cast = op is ">" or ">=" or "<" or "<=" ? "::numeric" : "";
            return negate
                ? $"NOT ({fieldExpr}{cast} {op} ${args.Count}{cast})"
                : $"{fieldExpr}{cast} {op} ${args.Count}{cast}";
        }
    }

    // Converts a dotted JSONB path like "body.user.email" into
    // payload::jsonb->'body'->'user'->>'email' (last segment uses ->>)
    private static string BuildJsonbPath(string column, string dotPath)
    {
        var segments = dotPath.Split('.');
        if (segments.Length == 0) return $"{column}::text";
        if (segments.Length == 1) return $"{column}::jsonb->>'{segments[0]}'";

        var sb = new System.Text.StringBuilder($"{column}::jsonb");
        for (var i = 0; i < segments.Length - 1; i++)
            sb.Append($"->'{segments[i]}'");
        sb.Append($"->>'{segments[^1]}'");
        return sb.ToString();
    }

    // ====================================================================
    // SQL ACL FILTERING
    // ====================================================================
    // Mirrors Python's apply_acl_and_query_policies. Adds a WHERE clause
    // that restricts rows to those the user owns OR has ACL access to OR
    // matches a query_policy pattern. Skipped for attachments and histories
    // (matching Python) and for the spaces query type.

    public static void AppendAclFilter(
        System.Text.StringBuilder sql, List<NpgsqlParameter> args,
        string? userShortname, string tableName, List<string>? queryPolicies)
    {
        // Python skips ACL for attachments, histories, and spaces.
        if (tableName is "attachments" or "histories") return;

        if (string.IsNullOrEmpty(userShortname)) return;

        args.Add(new() { Value = userShortname });
        var userParam = args.Count;

        // Base conditions: user owns the row OR is in the ACL with 'query' action.
        var conditions = new List<string>
        {
            $"owner_shortname = ${userParam}",
            $"EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(acl::jsonb) = 'array' THEN acl::jsonb ELSE '[]'::jsonb END) AS elem WHERE elem->>'user_shortname' = ${userParam} AND (elem->'allowed_actions') ? 'query')",
        };

        // Add query_policies LIKE patterns if the user has any.
        if (queryPolicies is { Count: > 0 })
        {
            var likeConditions = new List<string>();
            for (var i = 0; i < queryPolicies.Count; i++)
            {
                var pattern = queryPolicies[i].Replace("%", "\\%").Replace("_", "\\_").Replace("*", "%");
                args.Add(new() { Value = pattern });
                likeConditions.Add($"qp LIKE ${args.Count}");
            }
            if (likeConditions.Count > 0)
            {
                conditions.Insert(1,
                    $"EXISTS (SELECT 1 FROM unnest(query_policies) AS qp WHERE {string.Join(" OR ", likeConditions)})");
            }
        }

        sql.Append($"AND ({string.Join(" OR ", conditions)}) ");
    }

    // ====================================================================
    // ORDER + PAGING
    // ====================================================================

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

    // ====================================================================
    // GENERIC RUN HELPERS
    // ====================================================================

    public static async Task<List<T>> RunQueryAsync<T>(
        Db db, string selectAllColumns, Query q,
        Func<NpgsqlDataReader, T> hydrate,
        CancellationToken ct,
        string? userShortname = null, string? tableName = null,
        List<string>? queryPolicies = null)
    {
        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args);
        var sql = new System.Text.StringBuilder($"{selectAllColumns} WHERE {where} ");

        // Apply ACL filtering if user info provided.
        if (userShortname is not null && tableName is not null)
            AppendAclFilter(sql, args, userShortname, tableName, queryPolicies);

        AppendOrderAndPaging(sql, q, args);

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<T>();
        while (await reader.ReadAsync(ct))
        {
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

    // ====================================================================
    // AGGREGATION QUERY BUILDER
    // ====================================================================
    // Mirrors Python's query_aggregation(). Builds:
    //   SELECT group_by_cols, FUNC(args) AS alias FROM table WHERE ... GROUP BY ...

    public static async Task<List<Dictionary<string, object>>> RunAggregationAsync(
        Db db, string tableName, Query q, CancellationToken ct)
    {
        if (q.AggregationData is null)
            return new();

        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args);

        // Build SELECT clause: group_by columns + aggregate functions
        var selectParts = new List<string>();

        // Group-by columns
        foreach (var gb in q.AggregationData.GroupBy)
        {
            var expr = gb.StartsWith('@') ? gb[1..] : ResolveFieldExpr(gb);
            selectParts.Add($"{expr} AS {SanitizeAlias(gb)}");
        }

        // Aggregate functions (reducers)
        foreach (var reducer in q.AggregationData.Reducers)
        {
            var funcName = MapReducerToSql(reducer.ReducerName);
            if (funcName is null) continue;

            string fieldExpr;
            if (reducer.Args.Count == 0)
                fieldExpr = "*";
            else
            {
                var arg0 = reducer.Args[0];
                if (arg0.StartsWith('@')) arg0 = arg0[1..];
                fieldExpr = ResolveFieldExpr(arg0);

                // Type casting for numeric aggregates
                if (funcName is "SUM" or "AVG")
                    fieldExpr = $"({fieldExpr})::numeric";
            }

            var alias = !string.IsNullOrEmpty(reducer.Alias) ? SanitizeAlias(reducer.Alias) : SanitizeAlias(reducer.ReducerName);
            selectParts.Add($"{funcName}({fieldExpr}) AS {alias}");
        }

        if (selectParts.Count == 0) return new();

        var sql = new System.Text.StringBuilder(
            $"SELECT {string.Join(", ", selectParts)} FROM {tableName} WHERE {where} ");

        // GROUP BY
        if (q.AggregationData.GroupBy.Count > 0)
        {
            var gbExprs = q.AggregationData.GroupBy
                .Select(gb => gb.StartsWith('@') ? gb[1..] : ResolveFieldExpr(gb));
            sql.Append($"GROUP BY {string.Join(", ", gbExprs)} ");
        }

        // ORDER + LIMIT
        AppendOrderAndPaging(sql, q, args);

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
            }
            results.Add(row);
        }
        return results;
    }

    // Maps Redis reducer names to PostgreSQL aggregate function names.
    private static string? MapReducerToSql(string reducerName) => reducerName.ToLowerInvariant() switch
    {
        "count" or "count_distinct" or "count_distinctish" or "r_count" => "COUNT",
        "sum" or "total" => "SUM",
        "avg" => "AVG",
        "min" => "MIN",
        "max" => "MAX",
        "stddev" => "STDDEV",
        "group_concat" or "tolist" => "STRING_AGG",
        _ => null,
    };

    // Resolves a field name (possibly dotted JSONB path) to a SQL expression.
    private static string ResolveFieldExpr(string field)
    {
        if (field.StartsWith("payload.body.", StringComparison.Ordinal))
            return BuildJsonbPath("payload", field["payload.".Length..]);
        if (field.StartsWith("payload.", StringComparison.Ordinal))
            return BuildJsonbPath("payload", field["payload.".Length..]);
        if (field.Contains('.'))
        {
            var dot = field.IndexOf('.');
            return BuildJsonbPath(field[..dot], field[(dot + 1)..]);
        }
        return field;
    }

    // Sanitize an alias for SQL (replace dots/at-signs with underscores).
    private static string SanitizeAlias(string s)
    {
        var result = Regex.Replace(s.Replace("@", "").Replace(".", "_"), @"[^a-zA-Z0-9_]", "_");
        if (result.Length > 0 && char.IsDigit(result[0])) result = "a_" + result;
        return result;
    }

    // BuildJsonbPath is defined in the search section above — reused by
    // both the search parser and the aggregation builder.
}
