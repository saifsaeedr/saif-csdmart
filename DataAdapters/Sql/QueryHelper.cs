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
    // Two-phase architecture matching Python's parser:
    //   Phase 1: ParseSearchExpression → list of SearchGroups (handles parens, "and")
    //   Phase 2: SQL generation from SearchGroups

    // Token regex — extended to capture bracket ranges as a single token.
    // Matches (in order): @field:[range] | @field:"quoted" | @field:value | plain_word
    private static readonly Regex SearchTokenRegex = new(
        @"-?@[^:\s]+:\[[^\]]*\]|-?@[^:\s]+:""[^""]*""|-?@[^:\s]+:[^\s]+|\S+",
        RegexOptions.Compiled);

    private static readonly Regex ComparisonRegex = new(
        @"^(>=|<=|>|<|!)(.+)$", RegexOptions.Compiled);

    private static readonly Regex NumericRegex = new(
        @"^-?\d+(?:\.\d+)?$", RegexOptions.Compiled);

    private static readonly Regex RangeRegex = new(
        @"^\[(.+?)[\s,](.+?)\]$", RegexOptions.Compiled);

    // Known JSONB array columns across dmart tables.
    private static readonly HashSet<string> JsonbArrayColumns = new(StringComparer.Ordinal)
        { "tags", "roles", "groups", "query_policies" };

    private static readonly HashSet<string> BooleanColumns = new(StringComparer.Ordinal)
        { "is_active", "is_open" };

    // ── Parsed search data structures ──────────────────────────────────

    private sealed class SearchField
    {
        public List<string> Values { get; set; } = new();
        public string Operation { get; set; } = "AND";   // AND | OR | RANGE
        public bool Negative { get; set; }
        public string ValueType { get; set; } = "string"; // string | numeric | boolean
        public string? ComparisonOperator { get; set; }
        public bool IsRange { get; set; }
    }

    private sealed class SearchGroup
    {
        public Dictionary<string, SearchField> Fields { get; } = new(StringComparer.Ordinal);
        public List<string> TextTerms { get; } = new();
    }

    // ── Phase 1: Parse ─────────────────────────────────────────────────

    private static List<SearchGroup> ParseSearchExpression(string search)
    {
        bool hasParens = search.Contains('(') || search.Contains(')');

        if (!hasParens)
        {
            var tokens = SearchTokenRegex.Matches(search);
            var fieldTokens = new List<string>();
            var textTerms = new List<string>();
            foreach (Match t in tokens)
            {
                var v = t.Value;
                if (v.Equals("and", StringComparison.OrdinalIgnoreCase)) continue;
                if (v.StartsWith('@') || v.StartsWith("-@"))
                    fieldTokens.Add(v);
                else
                    textTerms.Add(v);
            }
            var group = new SearchGroup();
            ParseSearchString(fieldTokens, group.Fields);
            group.TextTerms.AddRange(textTerms);
            return new List<SearchGroup> { group };
        }

        // Parentheses grouping: AND within group, OR between groups.
        var normalized = search.Replace("(", " ( ").Replace(")", " ) ");
        var allTokens = SearchTokenRegex.Matches(normalized);

        var groups = new List<SearchGroup>();
        var curFields = new List<string>();
        var curText = new List<string>();
        int depth = 0;

        void Flush()
        {
            if (curFields.Count == 0 && curText.Count == 0) return;
            var g = new SearchGroup();
            ParseSearchString(curFields, g.Fields);
            g.TextTerms.AddRange(curText);
            groups.Add(g);
        }

        foreach (Match tok in allTokens)
        {
            var v = tok.Value;
            if (v == "(")
            {
                if (depth == 0) { Flush(); curFields.Clear(); curText.Clear(); }
                depth++;
                continue;
            }
            if (v == ")")
            {
                depth = Math.Max(0, depth - 1);
                if (depth == 0) { Flush(); curFields.Clear(); curText.Clear(); }
                continue;
            }
            if (v.Equals("and", StringComparison.OrdinalIgnoreCase)) continue;
            if (v.StartsWith('@') || v.StartsWith("-@"))
                curFields.Add(v);
            else
                curText.Add(v);
        }
        Flush();

        return groups.Count > 0 ? groups : new List<SearchGroup> { new() };
    }

    private static void ParseSearchString(List<string> tokens, Dictionary<string, SearchField> result)
    {
        foreach (var token in tokens)
        {
            var raw = token;
            var negative = raw.StartsWith("-@");
            if (negative) raw = raw[1..]; // strip leading '-'

            if (!raw.StartsWith('@')) continue;
            var colonIdx = raw.IndexOf(':', 1);
            if (colonIdx < 0) continue;

            var field = raw[1..colonIdx];
            var value = raw[(colonIdx + 1)..].Trim('"');

            // Check comparison operator (only when ! or value is numeric)
            string? compOp = null;
            var compMatch = ComparisonRegex.Match(value);
            if (compMatch.Success)
            {
                var potOp = compMatch.Groups[1].Value;
                var potVal = compMatch.Groups[2].Value;
                if (potOp == "!" || NumericRegex.IsMatch(potVal))
                {
                    compOp = potOp;
                    value = potVal;
                }
            }

            // Range: [v1 v2] or [v1,v2]
            var rangeMatch = RangeRegex.Match(value);
            if (rangeMatch.Success)
            {
                var v1 = rangeMatch.Groups[1].Value.Trim();
                var v2 = rangeMatch.Groups[2].Value.Trim();
                bool allNum = NumericRegex.IsMatch(v1) && NumericRegex.IsMatch(v2);
                result[field] = new SearchField
                {
                    Values = new() { v1, v2 },
                    Operation = "RANGE",
                    Negative = negative,
                    ValueType = allNum ? "numeric" : "string",
                    IsRange = true,
                };
                continue;
            }

            // OR values (pipe)
            var values = value.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim()).ToList();
            var operation = values.Count > 1 ? "OR" : "AND";

            // Detect value type
            var valueType = "string";
            bool allBool = values.All(v =>
                v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("false", StringComparison.OrdinalIgnoreCase));
            bool allNumeric = values.All(v => NumericRegex.IsMatch(v));
            if (allBool) valueType = "boolean";
            else if (allNumeric) valueType = "numeric";

            // Same-field accumulation (for `@k:v1 and @k:v2`)
            if (result.TryGetValue(field, out var existing))
            {
                if (existing.Negative != negative)
                {
                    result[field] = new SearchField
                    {
                        Values = values, Operation = operation, Negative = negative,
                        ValueType = valueType, ComparisonOperator = compOp,
                    };
                }
                else
                {
                    existing.Values.AddRange(values);
                    if (operation == "OR") existing.Operation = "OR";
                }
            }
            else
            {
                result[field] = new SearchField
                {
                    Values = values, Operation = operation, Negative = negative,
                    ValueType = valueType, ComparisonOperator = compOp,
                };
            }
        }
    }

    // ── Phase 2: SQL generation ────────────────────────────────────────

    private static void AppendSearchClauses(System.Text.StringBuilder sql, string search, List<NpgsqlParameter> args)
    {
        var groups = ParseSearchExpression(search);
        var allGroupSql = new List<string>();

        foreach (var group in groups)
        {
            var conditions = new List<string>();

            foreach (var (field, data) in group.Fields)
            {
                var clause = BuildSearchFieldSql(field, data, args);
                if (clause is not null) conditions.Add(clause);
            }

            foreach (var term in group.TextTerms)
            {
                args.Add(new() { Value = $"%{term}%" });
                conditions.Add(
                    $"(shortname ILIKE ${args.Count} OR payload::text ILIKE ${args.Count} OR displayname::text ILIKE ${args.Count} OR description::text ILIKE ${args.Count} OR tags::text ILIKE ${args.Count})");
            }

            if (conditions.Count > 0)
                allGroupSql.Add("(" + string.Join(" AND ", conditions) + ")");
        }

        if (allGroupSql.Count == 0) return;
        if (allGroupSql.Count == 1)
            sql.Append($"AND {allGroupSql[0]} ");
        else
            sql.Append($"AND ({string.Join(" OR ", allGroupSql)}) ");
    }

    // ── Field-level SQL builders ───────────────────────────────────────

    private static string? BuildSearchFieldSql(string field, SearchField data, List<NpgsqlParameter> args)
    {
        if (data.Values.Count == 0) return null;

        // Existence check: @k:* → IS NOT NULL,  -@k:* → IS NULL
        if (data.Values.Count == 1 && data.Values[0] == "*" && !data.IsRange)
        {
            var nullCheck = data.Negative ? "IS NULL" : "IS NOT NULL";
            if (field.StartsWith("payload.", StringComparison.Ordinal))
            {
                var parts = field["payload.".Length..].Split('.');
                var arrowPath = string.Join("->", parts.Select(p => $"'{p}'"));
                return $"payload::jsonb->{arrowPath} {nullCheck}";
            }
            return $"{field} {nullCheck}";
        }

        // Payload JSONB paths
        if (field.StartsWith("payload.", StringComparison.Ordinal))
            return BuildPayloadSql(field["payload.".Length..], data, args);

        // JSONB array columns (tags, roles, groups, query_policies)
        if (JsonbArrayColumns.Contains(field))
            return BuildJsonbArraySql(field, data, args);

        // Dotted path into a JSONB column (e.g. collaborators.user1)
        if (field.Contains('.'))
        {
            var dot = field.IndexOf('.');
            var col = field[..dot];
            var sub = field[(dot + 1)..];

            if (sub == "*")
                return BuildWildcardTextSql(col, data, args);

            var expr = BuildJsonbPath(col, sub);
            return BuildScalarSql(expr, data, args);
        }

        // Boolean columns
        if (BooleanColumns.Contains(field))
            return BuildBooleanColumnSql(field, data, args);

        // Default: direct column as text
        return BuildScalarSql($"{field}::text", data, args);
    }

    // — Payload (JSONB) ————————————————————————————————————————————————

    private static string? BuildPayloadSql(string path, SearchField data, List<NpgsqlParameter> args)
    {
        var parts = path.Split('.');

        // Array iteration: any part ending in `[]` triggers
        // EXISTS (SELECT 1 FROM jsonb_array_elements(...) AS x WHERE ...) —
        // Python parity for `@payload.body.variants[].X:Y` and
        // `@payload.body.variants[]:Y` (primitive-array element match).
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].EndsWith("[]", StringComparison.Ordinal))
                return BuildPayloadArraySql(parts, i, data, args);
        }

        // Wildcard: payload.body.*  or  payload.*
        if (parts.Contains("*"))
        {
            var wildcardIdx = Array.IndexOf(parts, "*");
            string baseExpr;
            if (wildcardIdx == 0)
            {
                baseExpr = "payload::jsonb";
            }
            else
            {
                var sb = new System.Text.StringBuilder("payload::jsonb");
                for (int i = 0; i < wildcardIdx; i++) sb.Append($"->'{parts[i]}'");
                baseExpr = sb.ToString();
            }
            return BuildWildcardTextSql($"({baseExpr})", data, args);
        }

        // Build extraction path:  payload::jsonb->'body'->'k'  (arrow form for typeof)
        //                          payload::jsonb->'body'->>'k' (text extraction)
        var arrowPath = string.Join("->", parts.Select(p => $"'{p}'"));
        string textExtract;
        if (parts.Length > 1)
        {
            var nested = string.Join("->", parts[..^1].Select(p => $"'{p}'"));
            textExtract = $"payload::jsonb->{nested}->>'{parts[^1]}'";
        }
        else
        {
            textExtract = $"payload::jsonb->>'{parts[0]}'";
        }

        // Range query: BETWEEN
        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2)
                    (v1, v2) = (v2, v1);
                args.Add(new() { Value = v1 });
                var p1 = args.Count;
                args.Add(new() { Value = v2 });
                var p2 = args.Count;
                var cond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'number' AND (payload::jsonb->{arrowPath})::float {(data.Negative ? "NOT " : "")}BETWEEN CAST(${p1} AS float) AND CAST(${p2} AS float))";
                return cond;
            }
            // String/date range — lexicographic BETWEEN on text extraction
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            args.Add(new() { Value = v1 });
            var sp1 = args.Count;
            args.Add(new() { Value = v2 });
            var sp2 = args.Count;
            return $"({textExtract} {(data.Negative ? "NOT " : "")}BETWEEN ${sp1} AND ${sp2})";
        }

        // Type-aware value matching (mirrors Python's jsonb_typeof checks)
        return BuildPayloadValueSql(arrowPath, textExtract, data, args);
    }

    // Handles `@payload.a.b[].c:value` and `@payload.a.b[]:value` forms.
    //   * arrayIdx = index into `parts` of the element ending in `[]`
    //   * everything before that is the path to the JSONB array
    //   * everything after is the path to dereference on each element
    // Emits EXISTS (SELECT 1 FROM jsonb_array_elements(...) AS x WHERE <cond>).
    // Supports equality (numeric/string), comparison operators (>, <, >=, <=),
    // negation, and ranges `[A B]`.
    private static string? BuildPayloadArraySql(
        string[] parts, int arrayIdx, SearchField data, List<NpgsqlParameter> args)
    {
        // Prefix: path to the array itself. Strip `[]` off the array part.
        var prefixParts = new List<string>(arrayIdx + 1);
        for (int i = 0; i < arrayIdx; i++) prefixParts.Add(parts[i]);
        prefixParts.Add(parts[arrayIdx][..^2]); // drop "[]"
        var arrayPathArrow = string.Join("->", prefixParts.Select(p => $"'{p}'"));
        var arrayExpr = $"payload::jsonb->{arrayPathArrow}";

        var remaining = parts.Skip(arrayIdx + 1).ToArray();
        bool hasSubPath = remaining.Length > 0;

        // Per-element expressions for inside the EXISTS subquery.
        //   hasSubPath  → x->'a'->'b'->>'c'  (text)  /  x->'a'->'b'->'c' (jsonb, for typeof/float cast)
        //   primitive   → e (alias used with jsonb_array_elements_text)
        string elementText, elementJsonb, iterator;
        if (hasSubPath)
        {
            if (remaining.Length == 1)
            {
                elementText = $"x->>'{remaining[0]}'";
                elementJsonb = $"x->'{remaining[0]}'";
            }
            else
            {
                var nested = string.Join("->", remaining[..^1].Select(p => $"'{p}'"));
                elementText = $"x->{nested}->>'{remaining[^1]}'";
                elementJsonb = $"x->{nested}->'{remaining[^1]}'";
            }
            iterator = $"jsonb_array_elements({arrayExpr}) AS x";
        }
        else
        {
            // Primitive-array path: compare scalar elements directly.
            elementText = "e";
            elementJsonb = "e::jsonb";
            iterator = $"jsonb_array_elements_text({arrayExpr}) AS e";
        }

        var typeofGuard = $"jsonb_typeof({arrayExpr}) = 'array'";

        // Range: emit EXISTS (... BETWEEN ...)
        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2)
                    (v1, v2) = (v2, v1);
                args.Add(new() { Value = v1 });
                var p1 = args.Count;
                args.Add(new() { Value = v2 });
                var p2 = args.Count;
                var castCol = hasSubPath ? $"({elementJsonb})::float" : "e::float";
                var between = $"{castCol} BETWEEN CAST(${p1} AS float) AND CAST(${p2} AS float)";
                var exists = $"EXISTS (SELECT 1 FROM {iterator} WHERE {between})";
                return data.Negative
                    ? $"({typeofGuard} AND NOT {exists})"
                    : $"({typeofGuard} AND {exists})";
            }
            // Lexicographic range for string/date.
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            args.Add(new() { Value = v1 });
            var sp1 = args.Count;
            args.Add(new() { Value = v2 });
            var sp2 = args.Count;
            var between2 = $"{elementText} BETWEEN ${sp1} AND ${sp2}";
            var exists2 = $"EXISTS (SELECT 1 FROM {iterator} WHERE {between2})";
            return data.Negative
                ? $"({typeofGuard} AND NOT {exists2})"
                : $"({typeofGuard} AND {exists2})";
        }

        var compOp = data.ComparisonOperator;
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            bool isNum = NumericRegex.IsMatch(value);
            string predicate;

            if (isNum && compOp is not null)
            {
                var sqlOp = compOp switch { "!" => "!=", ">" => ">", ">=" => ">=", "<" => "<", "<=" => "<=", _ => "=" };
                args.Add(new() { Value = double.Parse(value) });
                var pNum = args.Count;
                var castCol = hasSubPath ? $"({elementJsonb})::float" : "e::float";
                predicate = $"{castCol} {sqlOp} CAST(${pNum} AS float)";
            }
            else if (data.Negative || compOp == "!")
            {
                args.Add(new() { Value = value });
                predicate = $"{elementText} != ${args.Count}";
            }
            else if (isNum)
            {
                args.Add(new() { Value = double.Parse(value) });
                var pNum = args.Count;
                var castCol = hasSubPath ? $"({elementJsonb})::float" : "e::float";
                predicate = $"{castCol} = CAST(${pNum} AS float)";
            }
            else
            {
                args.Add(new() { Value = value });
                predicate = $"{elementText} = ${args.Count}";
            }

            var exists = $"EXISTS (SELECT 1 FROM {iterator} WHERE {predicate})";
            conditions.Add(data.Negative
                ? $"({typeofGuard} AND NOT {exists})"
                : $"({typeofGuard} AND {exists})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    private static string? BuildPayloadValueSql(
        string arrowPath, string textExtract, SearchField data, List<NpgsqlParameter> args)
    {
        var conditions = new List<string>();
        var compOp = data.ComparisonOperator;

        if (data.ValueType == "boolean")
        {
            foreach (var v in data.Values)
            {
                var bv = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                args.Add(new() { Value = bv, NpgsqlDbType = NpgsqlDbType.Boolean });
                var p = args.Count;
                var eq = (data.Negative || compOp == "!") ? "!=" : "=";
                conditions.Add(
                    $"((jsonb_typeof(payload::jsonb->{arrowPath}) = 'boolean' AND ({textExtract})::boolean {eq} ${p}) OR " +
                    $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'string' AND ({textExtract})::boolean {eq} ${p}))");
            }
            return JoinConditions(conditions, data.Operation, data.Negative);
        }

        foreach (var value in data.Values)
        {
            bool isNum = NumericRegex.IsMatch(value);

            args.Add(new() { Value = value });
            var pVal = args.Count;
            args.Add(new() { Value = ToJsonArray(value) });
            var pJsonArr = args.Count;

            // Numeric comparison operator
            if (isNum && compOp is not null)
            {
                var sqlOp = compOp switch { "!" => "!=", ">" => ">", ">=" => ">=", "<" => "<", "<=" => "<=", _ => "=" };
                args.Add(new() { Value = double.Parse(value) });
                var pNum = args.Count;
                conditions.Add(
                    $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'number' AND ({textExtract})::float {sqlOp} CAST(${pNum} AS float))");
            }
            else if (data.Negative || compOp == "!")
            {
                // Negation: NOT in array AND != as string
                var arrayCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'array' AND NOT (payload::jsonb->{arrowPath} @> CAST(${pJsonArr} AS jsonb)))";
                var stringCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'string' AND {textExtract} != ${pVal})";
                if (isNum)
                {
                    args.Add(new() { Value = double.Parse(value) });
                    var pNum = args.Count;
                    var numCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'number' AND ({textExtract})::float != CAST(${pNum} AS float))";
                    conditions.Add($"({arrayCond} OR {stringCond} OR {numCond})");
                }
                else
                {
                    conditions.Add($"({arrayCond} OR {stringCond})");
                }
            }
            else
            {
                // Positive match: in array OR == string OR direct jsonb match
                var arrayCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'array' AND payload::jsonb->{arrowPath} @> CAST(${pJsonArr} AS jsonb))";
                var stringCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'string' AND {textExtract} = ${pVal})";
                args.Add(new() { Value = ToJsonString(value) });
                var pJsonDirect = args.Count;
                var directCond = $"(payload::jsonb->{arrowPath} = CAST(${pJsonDirect} AS jsonb))";

                if (isNum)
                {
                    args.Add(new() { Value = double.Parse(value) });
                    var pNum = args.Count;
                    var numCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'number' AND ({textExtract})::float = CAST(${pNum} AS float))";
                    conditions.Add($"({arrayCond} OR {stringCond} OR {directCond} OR {numCond})");
                }
                else
                {
                    conditions.Add($"({arrayCond} OR {stringCond} OR {directCond})");
                }
            }
        }

        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — JSONB array columns (tags, roles, groups) ——————————————————————

    private static string? BuildJsonbArraySql(string column, SearchField data, List<NpgsqlParameter> args)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            args.Add(new() { Value = value });
            var pVal = args.Count;
            args.Add(new() { Value = ToJsonArray(value) });
            var pJson = args.Count;

            if (data.Negative)
            {
                conditions.Add(
                    $"((jsonb_typeof({column}) = 'array' AND NOT ({column} @> CAST(${pJson} AS jsonb))) OR " +
                    $"(jsonb_typeof({column}) = 'object' AND NOT ({column}::text ILIKE '%' || ${pVal} || '%')))");
            }
            else
            {
                conditions.Add(
                    $"((jsonb_typeof({column}) = 'array' AND {column} @> CAST(${pJson} AS jsonb)) OR " +
                    $"(jsonb_typeof({column}) = 'object' AND {column}::text ILIKE '%' || ${pVal} || '%'))");
            }
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Wildcard text search on a JSONB subtree ———————————————————————

    private static string? BuildWildcardTextSql(string baseExpr, SearchField data, List<NpgsqlParameter> args)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            args.Add(new() { Value = $"%{value}%" });
            conditions.Add(data.Negative
                ? $"({baseExpr}::text NOT ILIKE ${args.Count})"
                : $"({baseExpr}::text ILIKE ${args.Count})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Boolean column ————————————————————————————————————————————————

    private static string? BuildBooleanColumnSql(string column, SearchField data, List<NpgsqlParameter> args)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var bv = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            args.Add(new() { Value = bv, NpgsqlDbType = NpgsqlDbType.Boolean });
            var eq = (data.Negative || data.ComparisonOperator == "!") ? "!=" : "=";
            conditions.Add($"(CAST({column} AS BOOLEAN) {eq} ${args.Count})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Scalar text/numeric column ————————————————————————————————————

    private static string? BuildScalarSql(string fieldExpr, SearchField data, List<NpgsqlParameter> args)
    {
        var compOp = data.ComparisonOperator;
        var conditions = new List<string>();

        // Range
        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2)
                    (v1, v2) = (v2, v1);
                args.Add(new() { Value = v1 });
                var p1 = args.Count;
                args.Add(new() { Value = v2 });
                var p2 = args.Count;
                return $"(CAST({fieldExpr} AS FLOAT) {(data.Negative ? "NOT " : "")}BETWEEN CAST(${p1} AS float) AND CAST(${p2} AS float))";
            }
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            args.Add(new() { Value = v1 });
            var sp1 = args.Count;
            args.Add(new() { Value = v2 });
            var sp2 = args.Count;
            return $"({fieldExpr} {(data.Negative ? "NOT " : "")}BETWEEN ${sp1} AND ${sp2})";
        }

        foreach (var value in data.Values)
        {
            if (compOp is not null && compOp != "!")
            {
                // Numeric comparison
                args.Add(new() { Value = value });
                var cast = "::numeric";
                conditions.Add(data.Negative
                    ? $"NOT ({fieldExpr}{cast} {compOp} ${args.Count}{cast})"
                    : $"{fieldExpr}{cast} {compOp} ${args.Count}{cast}");
            }
            else if (data.Negative || compOp == "!")
            {
                args.Add(new() { Value = value });
                conditions.Add($"{fieldExpr} != ${args.Count}");
            }
            else
            {
                // Default: exact match for non-payload text columns
                args.Add(new() { Value = $"%{value}%" });
                conditions.Add($"{fieldExpr} ILIKE ${args.Count}");
            }
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — JSON literal helpers (AOT-safe, no JsonSerializer) —————————————

    private static string ToJsonArray(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"[\"{escaped}\"]";
    }

    private static string ToJsonString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    // — Join helpers ——————————————————————————————————————————————————

    private static string? JoinConditions(List<string> conditions, string operation, bool negative)
    {
        if (conditions.Count == 0) return null;
        if (conditions.Count == 1) return conditions[0];

        // Python logic: when negative, swap AND↔OR for joining
        string joinOp;
        if (negative)
            joinOp = operation == "AND" ? " OR " : " AND ";
        else
            joinOp = operation == "AND" ? " AND " : " OR ";

        return "(" + string.Join(joinOp, conditions) + ")";
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

        AppendOrderAndPaging(sql, q, args, tableName);

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            try { results.Add(hydrate(reader)); }
            catch (Exception ex) { Console.Error.WriteLine($"WARN: skipped row with bad data: {ex.Message}"); }
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
