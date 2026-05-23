using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// Shared query-building logic used by every repository's QueryAsync/CountQueryAsync.
// Mirrors Python's set_sql_statement_from_query + apply_acl_and_query_policies +
// query_aggregation. Every table in dmart shares the same Metas-base columns, so
// the filter logic is identical; only the FROM clause differs.
public static class QueryHelper
{
    private static ILogger _log = NullLogger.Instance;

    // Called once at startup from Program.cs to wire structured logging.
    public static void SetLogger(ILoggerFactory factory) =>
        _log = factory.CreateLogger("Dmart.QueryHelper");

    // ====================================================================
    // WHERE CLAUSE BUILDER
    // ====================================================================

    public static string BuildWhereClause(Query q, List<NpgsqlParameter> args, string? tableName = null)
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
            AppendSearchClauses(sql, q.Search, args, tableName);

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
    // SEARCH-CLAUSE DELEGATION
    // ====================================================================
    // The full RediSearch-flavoured grammar used to live inline here; it now
    // lives in Dmart.QueryGrammar.SearchExpressionParser, shared with the
    // SDK so the two cannot drift. The server uses positional $N parameters
    // so we ask the parser for that style.

    private static void AppendSearchClauses(
        System.Text.StringBuilder sql, string search, List<NpgsqlParameter> args, string? tableName = null)
    {
        var parsed = SearchExpressionParser.Parse(
            search, args.Count, PlaceholderStyle.Positional, tableName);

        // Always append params (parser may bind even when clauses end up empty —
        // e.g. an all-negative group; the SDK side does the same).
        foreach (var p in parsed.Parameters) args.Add(p);

        if (parsed.Clauses.Count == 0) return;

        // The parser returns either a single AND-joined group or a
        // ("groupA" OR "groupB" ...) compound. Either way we just prefix AND
        // and join with spaces — matches the previous inline behaviour
        // verbatim. Parens were already added by the parser for the OR case.
        sql.Append("AND ");
        sql.Append(string.Join(' ', parsed.Clauses));
        sql.Append(' ');
    }

    // ====================================================================
    // SHARED SQL UTILITIES
    // ====================================================================
    // Used by the aggregation builder below (and historically by the inline
    // search parser, now extracted). Kept here because aggregation field
    // resolution still needs them.

    // Strict SQL-identifier validator. Any `field` interpolated into the SQL
    // (as a column name or cast target) MUST match — without this gate a
    // crafted token could inject arbitrary SQL. Pattern matches a valid
    // lowercase Postgres column identifier up to NAMEDATALEN.
    private static readonly Regex SafeColumnIdent = new(
        @"^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);

    // Escape a string for use inside a single-quoted SQL literal. Doubles any
    // apostrophes per PostgreSQL's standard escape rule. Used for JSONB-path
    // segments that can't be parameterised (they're part of the operator
    // expression, not data).
    private static string EscapeSqlLiteral(string s) => s.Replace("'", "''");

    // Converts a dotted JSONB path like "body.user.email" into
    // payload::jsonb->'body'->'user'->>'email' (last segment uses ->>).
    private static string BuildJsonbPath(string column, string dotPath)
    {
        var segments = dotPath.Split('.');
        if (segments.Length == 0) return $"{column}::text";
        if (segments.Length == 1) return $"{column}::jsonb->>'{EscapeSqlLiteral(segments[0])}'";

        var sb = new System.Text.StringBuilder($"{column}::jsonb");
        for (var i = 0; i < segments.Length - 1; i++)
            sb.Append($"->'{EscapeSqlLiteral(segments[i])}'");
        sb.Append($"->>'{EscapeSqlLiteral(segments[^1])}'");
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
                // Build a LIKE pattern from the dmart wildcard ('*' → '%').
                // Order matters: escape backslash FIRST (otherwise the replacements
                // below introduce new backslashes that get double-escaped), then
                // the LIKE metacharacters %, _, finally expand '*'.
                var pattern = queryPolicies[i]
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_")
                    .Replace("*", "%");
                args.Add(new() { Value = pattern });
                // ESCAPE '\' so the backslash escapes above are honored by LIKE.
                likeConditions.Add($"qp LIKE ${args.Count} ESCAPE '\\'");
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

    // Per-table whitelists of column names accepted for bare-column sort_by.
    // JSON-path tokens (anything containing a dot) are NOT gated by this list —
    // they're handled by BuildJsonPathSortExpression, which sanitizes each path
    // segment via SafeSortSegmentRegex so a hostile wire value can't smuggle
    // arbitrary SQL. When a comma-separated sort_by lists an unknown bare
    // column AND no JSON-path token resolves, we fall back to `updated_at`.
    private static readonly HashSet<string> SharedSortColumns = new(StringComparer.Ordinal)
    {
        "shortname", "created_at", "updated_at", "displayname", "description",
        "is_active", "resource_type", "owner_shortname", "owner_group_shortname",
        "uuid", "slug", "payload_content_type"
    };
    private static readonly Dictionary<string, HashSet<string>> TableSortColumns = new(StringComparer.Ordinal)
    {
        ["entries"] = new(SharedSortColumns, StringComparer.Ordinal) { "schema_shortname", "state", "payload" },
        ["attachments"] = new(SharedSortColumns, StringComparer.Ordinal) { "schema_shortname", "payload" },
        ["users"] = new(SharedSortColumns, StringComparer.Ordinal) { "email", "msisdn", "type", "language", "payload" },
        ["spaces"] = new(SharedSortColumns, StringComparer.Ordinal) { "space_name", "subpath", "payload" },
        ["roles"] = new(SharedSortColumns, StringComparer.Ordinal),
        ["permissions"] = new(SharedSortColumns, StringComparer.Ordinal),
        ["histories"] = new(SharedSortColumns, StringComparer.Ordinal),
    };

    // Only alphanumerics and underscore allowed per path segment — matches
    // Python's adapter_helpers._sanitize_sql_part. Keeps the segment safe for
    // inlining as a JSONB key literal inside the emitted SQL.
    private static readonly Regex SafeSortSegmentRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // Build the SQL expression for a JSON-path sort token like "payload.body.rank".
    // Mirrors Python's transform_keys_to_sql + sort CASE wrap:
    //   payload -> 'body' ->> 'rank'
    //   CASE WHEN (<expr>) ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (<expr>)::float END <dir>, (<expr>) <dir>
    // The CASE makes numeric values sort numerically (1,2,10) while non-numeric
    // values still sort lexically as a tiebreaker. Returns null when any segment
    // fails validation (sanitizer rejects the whole token, we keep other tokens).
    private static string? BuildJsonPathSortExpression(string token, string direction)
    {
        var parts = token.Split('.');
        foreach (var p in parts)
            if (!SafeSortSegmentRegex.IsMatch(p)) return null;

        var root = parts[0];
        string expr;
        if (parts.Length == 1)
        {
            expr = root;
        }
        else
        {
            var middle = parts.Length > 2 ? " -> " + string.Join(" -> ", parts[1..^1].Select(p => $"'{p}'")) : "";
            expr = $"{root}::jsonb{middle} ->> '{parts[^1]}'";
        }
        return $"CASE WHEN ({expr}) ~ '^-?[0-9]+(\\.[0-9]+)?$' THEN ({expr})::float END {direction}, ({expr}) {direction}";
    }

    // Resolve a single sort token into either a bare whitelisted column or a
    // JSON-path expression. Returns null to mean "skip this token".
    private static string? ResolveSortToken(string rawToken, string direction, string? tableName)
    {
        var token = rawToken.Trim();
        if (token.Length == 0) return null;

        // Python: sort_by.replace("attributes.", "") — strip anywhere (the dmart
        // convention is to mirror the wire envelope's attributes.* into the
        // storage columns/payload), then drop a leading '@' for consistency
        // with the search-token syntax.
        token = token.Replace("attributes.", "");
        if (token.StartsWith('@')) token = token[1..];

        // Python shortcut: "body.xxx" is sugar for "payload.body.xxx".
        if (token.StartsWith("body.", StringComparison.Ordinal)) token = "payload." + token;

        if (token.Contains('.'))
            return BuildJsonPathSortExpression(token, direction);

        var allowed = tableName is not null && TableSortColumns.TryGetValue(tableName, out var set)
            ? set
            : SharedSortColumns;
        return allowed.Contains(token) ? $"{token} {direction}" : null;
    }

    // Parse comma-separated sort_by into one ORDER BY clause body (without the
    // leading "ORDER BY "). Returns null when nothing resolves → caller falls
    // back to `updated_at DESC`.
    private static string? BuildOrderClauseBody(string? sortBy, SortType? sortType, string? tableName)
    {
        if (string.IsNullOrWhiteSpace(sortBy)) return null;

        var direction = sortType == SortType.Ascending ? "ASC" : "DESC";
        var pieces = new List<string>();
        foreach (var raw in sortBy.Split(','))
        {
            var expr = ResolveSortToken(raw, direction, tableName);
            if (expr is not null) pieces.Add(expr);
        }
        return pieces.Count == 0 ? null : string.Join(", ", pieces);
    }

    public static void AppendOrderAndPaging(System.Text.StringBuilder sql, Query q, List<NpgsqlParameter> args, string? tableName = null)
    {
        if (q.Type == QueryType.Random)
            sql.Append("ORDER BY RANDOM() ");
        else
        {
            var clause = BuildOrderClauseBody(q.SortBy, q.SortType, tableName);
            if (clause is not null)
            {
                sql.Append($"ORDER BY {clause} ");
            }
            else
            {
                sql.Append("ORDER BY updated_at ");
                sql.Append(q.SortType == SortType.Ascending ? "ASC " : "DESC ");
            }
        }

        args.Add(new() { Value = Math.Max(1, q.Limit) });
        sql.Append($"LIMIT ${args.Count} ");
        args.Add(new() { Value = Math.Max(0, q.Offset) });
        sql.Append($"OFFSET ${args.Count}");
    }

    // ====================================================================
    // GENERIC RUN HELPERS
    // ====================================================================

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `selectAllColumns` and `tableName` are internal-only identifiers from typed repository callers; user values flow through $N parameters built by BuildWhereClause/AppendAclFilter.")]
    public static async Task<List<T>> RunQueryAsync<T>(
        Db db, string selectAllColumns, Query q,
        Func<NpgsqlDataReader, T> hydrate,
        CancellationToken ct,
        string? userShortname = null, string? tableName = null,
        List<string>? queryPolicies = null)
    {
        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args, tableName);
        var sql = new System.Text.StringBuilder($"{selectAllColumns} WHERE {where} ");

        // Apply ACL filtering if user info provided.
        if (userShortname is not null && tableName is not null)
            AppendAclFilter(sql, args, userShortname, tableName, queryPolicies);

        AppendOrderAndPaging(sql, q, args, tableName);

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            try { results.Add(hydrate(reader)); }
            catch (Exception ex) { _log.LogWarning(ex, "Skipped row with bad data"); }
        }
        return results;
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `tableName` is an internal-only identifier; user values flow through $N parameters.")]
    public static async Task<int> RunCountAsync(
        Db db, string tableName, Query q,
        CancellationToken ct,
        string? userShortname = null, List<string>? queryPolicies = null)
    {
        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args, tableName);
        var sqlBuilder = new System.Text.StringBuilder($"SELECT COUNT(*) FROM {tableName} WHERE {where} ");
        // Parity with RunQueryAsync: apply owner/ACL/query_policies predicate
        // so COUNT(*) is scoped to rows the actor can actually see. Skipped
        // for attachments/histories inside AppendAclFilter (Python parity).
        if (userShortname is not null)
            AppendAclFilter(sqlBuilder, args, userShortname, tableName, queryPolicies);

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sqlBuilder.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    // ====================================================================
    // AGGREGATION QUERY BUILDER
    // ====================================================================
    // Mirrors Python's query_aggregation(). Builds:
    //   SELECT group_by_cols, FUNC(args) AS alias FROM table WHERE ... GROUP BY ...

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `tableName` is internal; group-by/reducer expressions are built from a whitelisted ResolveFieldExpr/SanitizeAlias pipeline; user values flow through $N parameters.")]
    public static async Task<List<Dictionary<string, object>>> RunAggregationAsync(
        Db db, string tableName, Query q, CancellationToken ct,
        string? userShortname = null, List<string>? queryPolicies = null)
    {
        if (q.AggregationData is null)
            return new();

        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args, tableName);

        var groupBy = q.AggregationData.GroupBy ?? new();
        var reducers = q.AggregationData.Reducers ?? new();

        // Build SELECT clause: group_by columns + aggregate functions
        var selectParts = new List<string>();

        // Group-by columns
        foreach (var gb in groupBy)
        {
            var raw = gb.StartsWith('@') ? gb[1..] : gb;
            var expr = ResolveFieldExpr(raw);
            if (expr is null) continue;
            selectParts.Add($"{expr} AS {SanitizeAlias(gb)}");
        }

        // Aggregate functions (reducers)
        foreach (var reducer in reducers)
        {
            var alias = !string.IsNullOrEmpty(reducer.Alias) ? SanitizeAlias(reducer.Alias) : SanitizeAlias(reducer.ReducerName);
            var expr = BuildReducerExpression(reducer);
            if (expr is null) continue;
            selectParts.Add($"{expr} AS {alias}");
        }

        if (selectParts.Count == 0) return new();

        var sql = new System.Text.StringBuilder(
            $"SELECT {string.Join(", ", selectParts)} FROM {tableName} WHERE {where} ");

        if (userShortname is not null)
            AppendAclFilter(sql, args, userShortname, tableName, queryPolicies);

        // GROUP BY
        if (groupBy.Count > 0)
        {
            var gbExprs = groupBy
                .Select(gb => gb.StartsWith('@') ? gb[1..] : gb)
                .Select(ResolveFieldExpr)
                .Where(e => e is not null)
                .ToList();
            if (gbExprs.Count > 0)
                sql.Append($"GROUP BY {string.Join(", ", gbExprs)} ");
        }

        // ORDER + LIMIT
        AppendOrderAndPaging(sql, q, args, tableName);

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
                // Skip null columns rather than packing `null!` into a non-null
                // dictionary value — callers use TryGetValue and treat missing
                // keys the same as null.
                if (reader.IsDBNull(i)) continue;
                row[reader.GetName(i)] = reader.GetValue(i);
            }
            results.Add(row);
        }
        return results;
    }

    private static string? BuildReducerExpression(RedisReducer reducer)
    {
        var reducerArgs = reducer.Args ?? new();
        var name = reducer.ReducerName.ToLowerInvariant();

        string? ResolveArg(int index)
        {
            if (reducerArgs.Count <= index) return null;
            var arg = reducerArgs[index];
            if (arg.StartsWith('@')) arg = arg[1..];
            return ResolveFieldExpr(arg);
        }

        var fieldExpr = ResolveArg(0);
        return name switch
        {
            "count" or "r_count" => fieldExpr is null ? "COUNT(*)" : $"COUNT({fieldExpr})",
            "count_distinct" or "count_distinctish" =>
                fieldExpr is null ? "COUNT(*)" : $"COUNT(DISTINCT {fieldExpr})",
            "sum" or "total" =>
                fieldExpr is null ? null : $"SUM(({fieldExpr})::numeric)",
            "avg" =>
                fieldExpr is null ? null : $"AVG(({fieldExpr})::numeric)",
            "min" =>
                fieldExpr is null ? null : $"MIN({fieldExpr})",
            "max" =>
                fieldExpr is null ? null : $"MAX({fieldExpr})",
            "stddev" =>
                fieldExpr is null ? null : $"STDDEV(({fieldExpr})::numeric)",
            "group_concat" or "tolist" =>
                fieldExpr is null ? null : $"STRING_AGG(({fieldExpr})::text, ',')",
            "quantile" =>
                fieldExpr is null ? null : $"percentile_cont({ParseQuantile(reducerArgs)}) WITHIN GROUP (ORDER BY ({fieldExpr})::numeric)",
            "first_value" =>
                fieldExpr is null ? null : $"(ARRAY_AGG({fieldExpr} ORDER BY updated_at DESC))[1]",
            "random_sample" =>
                fieldExpr is null ? null : $"(ARRAY_AGG({fieldExpr} ORDER BY RANDOM()))[1]",
            _ => null,
        };
    }

    private static string ParseQuantile(List<string> args)
    {
        if (args.Count < 2) return "0.5";
        return decimal.TryParse(args[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var q)
            ? Math.Clamp(q, 0m, 1m).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0.5";
    }

    // Resolves a field name (possibly dotted JSONB path) to a SQL expression.
    private static string? ResolveFieldExpr(string field)
    {
        if (field.StartsWith("payload.body.", StringComparison.Ordinal))
            return BuildJsonbPath("payload", field["payload.".Length..]);
        if (field.StartsWith("payload.", StringComparison.Ordinal))
            return BuildJsonbPath("payload", field["payload.".Length..]);
        if (field.Contains('.'))
        {
            var dot = field.IndexOf('.');
            var col = field[..dot];
            if (!SafeColumnIdent.IsMatch(col)) return null;
            return BuildJsonbPath(col, field[(dot + 1)..]);
        }
        if (!SafeColumnIdent.IsMatch(field)) return null;
        return field;
    }

    // Sanitize an alias for SQL (replace dots/at-signs with underscores).
    private static string SanitizeAlias(string s)
    {
        var result = Regex.Replace(s.Replace("@", "").Replace(".", "_"), @"[^a-zA-Z0-9_]", "_");
        if (result.Length > 0 && char.IsDigit(result[0])) result = "a_" + result;
        return result;
    }
}
