using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.SqlAdapter.Helpers;

// Port of csdmart/DataAdapters/Sql/QueryHelper.cs — the RediSearch-flavoured
// `@field:value` parser the dmart HTTP server uses to build SQL WHERE clauses.
// The csdmart version emits positional `$N` placeholders against an
// args list. The demo SDK uses named parameters throughout DmartSqlAdapter
// (so it can coexist cleanly with PermissionFilter), so this port adapts
// the parameter style to named `@s_<n>` placeholders. Behaviour is
// otherwise identical, including:
//
//   - parenthesised groups (AND inside, OR between),
//   - negation (`-@field:value`),
//   - alternation (`@k:a|b`),
//   - ranges (`@k:[v1 v2]` / `@k:[v1,v2]`),
//   - comparison operators (`>` `<` `>=` `<=` `!`),
//   - wildcard tail value (`@k:abc*`),
//   - payload jsonb paths (`@payload.body.x.y:v`),
//   - payload array iteration (`@payload.body.items[].price:>100`),
//   - jsonb-array / text-array / boolean / timestamp columns,
//   - free-text plain words against shortname / payload / displayname /
//     description / tags.
//
// Extension over csdmart: `@msisdn:<v>` and `@email:<v>` resolve through the
// entries' owner_shortname → users(shortname) join, because dmart stores
// msisdn / email only on the users table and treating them as
// `<col>::text = $v` against entries would error. Anything else falls
// through to csdmart's exact behaviour, including the `<col>::text` path
// (which still errors against unknown columns — that's csdmart parity).
public static class SearchExpressionParser
{
    public sealed record Parsed(IReadOnlyList<string> Clauses, IReadOnlyList<NpgsqlParameter> Parameters);

    // ── Entry point ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses a RediSearch-style expression and returns SQL clause fragments
    /// plus the bound parameters they reference.
    /// </summary>
    /// <param name="expression">The search expression — see class docs for grammar.</param>
    /// <param name="startingParamIndex">
    /// Numeric suffix the parser appends to its placeholder names. Emitted
    /// parameters are named <c>@s_&lt;n&gt;</c> where <c>n</c> starts at this
    /// value and increments per bind. The CALLER is responsible for ensuring
    /// no name collision with their own parameters; today's caller
    /// (<c>DmartSqlAdapter.QueryAsync</c>) uses
    /// <c>@space</c> / <c>@subpath</c> / <c>@subpath_prefix</c> /
    /// <c>@rt&lt;i&gt;</c> / <c>@sn&lt;i&gt;</c> / <c>@schema&lt;i&gt;</c> /
    /// <c>@tags</c> / <c>@from</c> / <c>@to</c> / <c>@limit</c> /
    /// <c>@offset</c> — all collision-free with the <c>@s_*</c> namespace.
    /// </param>
    public static Parsed Parse(string expression, int startingParamIndex)
    {
        var clauses = new List<string>();
        var pars = new List<NpgsqlParameter>();
        if (string.IsNullOrWhiteSpace(expression)) return new Parsed(clauses, pars);

        var ctx = new ParamCtx(startingParamIndex);

        var groups = ParseSearchExpression(expression);
        var allGroupSql = new List<string>();
        foreach (var group in groups)
        {
            var conditions = new List<string>();

            foreach (var (field, data) in group.Fields)
            {
                var clause = BuildSearchFieldSql(field, data, ctx);
                if (clause is not null) conditions.Add(clause);
            }

            foreach (var term in group.TextTerms)
            {
                var p = ctx.Add($"%{term}%");
                conditions.Add(
                    $"(shortname ILIKE {p} OR payload::text ILIKE {p} OR displayname::text ILIKE {p} OR description::text ILIKE {p} OR tags::text ILIKE {p})");
            }

            if (conditions.Count > 0)
                allGroupSql.Add("(" + string.Join(" AND ", conditions) + ")");
        }

        pars.AddRange(ctx.Parameters);
        if (allGroupSql.Count == 0) return new Parsed(clauses, pars);
        if (allGroupSql.Count == 1) clauses.Add(allGroupSql[0]);
        else clauses.Add("(" + string.Join(" OR ", allGroupSql) + ")");

        return new Parsed(clauses, pars);
    }

    // ── Parameter bookkeeping ─────────────────────────────────────────────

    private sealed class ParamCtx
    {
        private int _next;
        public List<NpgsqlParameter> Parameters { get; } = new();
        public ParamCtx(int start) { _next = start; }

        // Bind a value, return the @name to splice into SQL.
        public string Add(object? value, NpgsqlDbType? dbType = null)
        {
            var name = "@s_" + _next.ToString(CultureInfo.InvariantCulture);
            _next++;
            NpgsqlParameter p;
            if (dbType.HasValue)
            {
                // Set the type BEFORE Value so Npgsql doesn't pre-infer text
                // and then refuse the cast. Critical for jsonb (containment
                // params for `payload @> $literal`) and boolean.
                p = new NpgsqlParameter(name, dbType.Value) { Value = value ?? DBNull.Value };
            }
            else
            {
                p = new NpgsqlParameter(name, value ?? DBNull.Value);
            }
            Parameters.Add(p);
            return name;
        }
    }

    // ── Regex & column whitelists ─────────────────────────────────────────

    // Matches (in order): @field:[range] | @field:"quoted" | @field:value | plain_word
    private static readonly Regex SearchTokenRegex = new(
        @"-?@[^:\s]+:\[[^\]]*\]|-?@[^:\s]+:""[^""]*""|-?@[^:\s]+:[^\s]+|\S+",
        RegexOptions.Compiled);

    private static readonly Regex ComparisonRegex = new(@"^(>=|<=|>|<|!)(.+)$", RegexOptions.Compiled);
    private static readonly Regex NumericRegex = new(@"^-?\d+(?:\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex RangeRegex = new(@"^\[(.+?)[\s,](.+?)\]$", RegexOptions.Compiled);

    private static readonly HashSet<string> JsonbArrayColumns = new(StringComparer.Ordinal)
        { "tags", "roles", "groups" };

    private static readonly HashSet<string> TextArrayColumns = new(StringComparer.Ordinal)
        { "query_policies" };

    private static readonly HashSet<string> BooleanColumns = new(StringComparer.Ordinal)
        { "is_active", "is_open" };

    private static readonly HashSet<string> TimestampColumns = new(StringComparer.Ordinal)
        { "created_at", "updated_at", "timestamp" };

    // Fields that live on users(shortname=owner_shortname). Resolved with an
    // owner_shortname IN (SELECT shortname FROM users WHERE <col> = $v) join.
    private static readonly HashSet<string> UserMetaColumns = new(StringComparer.Ordinal)
        { "msisdn", "email" };

    private static readonly Regex SafeColumnIdent = new(
        @"^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);

    private static string EscapeSqlLiteral(string s) => s.Replace("'", "''");

    // ── Parsed data structures ────────────────────────────────────────────

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

    // ── Phase 1: Parse ────────────────────────────────────────────────────

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
            if (v.StartsWith('@') || v.StartsWith("-@")) curFields.Add(v);
            else curText.Add(v);
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
            if (negative) raw = raw[1..];

            if (!raw.StartsWith('@')) continue;
            var colonIdx = raw.IndexOf(':', 1);
            if (colonIdx < 0) continue;

            var field = raw[1..colonIdx];
            var value = raw[(colonIdx + 1)..].Trim('"');

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

            var values = value.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim()).ToList();
            var operation = values.Count > 1 ? "OR" : "AND";

            var valueType = "string";
            bool allBool = values.All(v =>
                v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("false", StringComparison.OrdinalIgnoreCase));
            bool allNumeric = values.All(v => NumericRegex.IsMatch(v));
            if (allBool) valueType = "boolean";
            else if (allNumeric) valueType = "numeric";

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

    // ── Phase 2: SQL generation ───────────────────────────────────────────

    private static string? BuildSearchFieldSql(string field, SearchField data, ParamCtx ctx)
    {
        if (data.Values.Count == 0) return null;

        // Existence check: @k:* → IS NOT NULL,  -@k:* → IS NULL
        if (data.Values.Count == 1 && data.Values[0] == "*" && !data.IsRange)
        {
            var nullCheck = data.Negative ? "IS NULL" : "IS NOT NULL";
            if (field.StartsWith("payload.", StringComparison.Ordinal))
            {
                var parts = field["payload.".Length..].Split('.');
                var arrowPath = string.Join("->", parts.Select(p => $"'{EscapeSqlLiteral(p)}'"));
                return $"payload::jsonb->{arrowPath} {nullCheck}";
            }
            if (!SafeColumnIdent.IsMatch(field)) return null;
            if (TextArrayColumns.Contains(field))
            {
                var lengthExpr = $"COALESCE(array_length({field}, 1), 0)";
                return data.Negative ? $"{lengthExpr} = 0" : $"{lengthExpr} > 0";
            }
            return $"{field} {nullCheck}";
        }

        // Extension: user-meta fields live on `users`. Resolve via owner.
        if (UserMetaColumns.Contains(field))
            return BuildUserMetaSql(field, data, ctx);

        // Payload JSONB paths
        if (field.StartsWith("payload.", StringComparison.Ordinal))
            return BuildPayloadSql(field["payload.".Length..], data, ctx);

        if (JsonbArrayColumns.Contains(field))
            return BuildJsonbArraySql(field, data, ctx);

        if (TextArrayColumns.Contains(field))
            return BuildTextArraySql(field, data, ctx);

        if (field.Contains('.'))
        {
            var dot = field.IndexOf('.');
            var col = field[..dot];
            var sub = field[(dot + 1)..];
            if (!SafeColumnIdent.IsMatch(col)) return null;
            if (sub == "*") return BuildWildcardTextSql(col, data, ctx);
            var expr = BuildJsonbPath(col, sub);
            return BuildScalarSql(expr, data, ctx);
        }

        if (BooleanColumns.Contains(field))
            return BuildBooleanColumnSql(field, data, ctx);

        if (TimestampColumns.Contains(field))
            return BuildTimestampColumnSql(field, data, ctx);

        if (!SafeColumnIdent.IsMatch(field)) return null;
        return BuildScalarSql($"{field}::text", data, ctx);
    }

    // — User-meta join (extension over csdmart) ———————————————————————————

    private static string? BuildUserMetaSql(string column, SearchField data, ParamCtx ctx)
    {
        // Don't bother with ranges/comparisons here — msisdn/email are
        // simple identifier strings. If you need richer semantics, query
        // the users table directly via a future UserRepository.
        if (data.IsRange || data.ComparisonOperator is { } op && op != "!") return null;

        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var p = ctx.Add(value);
            var negate = data.Negative || data.ComparisonOperator == "!";
            var inner = $"SELECT shortname FROM users WHERE {column} = {p}";
            conditions.Add(negate
                ? $"owner_shortname NOT IN ({inner})"
                : $"owner_shortname IN ({inner})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Payload (JSONB) ————————————————————————————————————————————————

    private static string? BuildPayloadSql(string path, SearchField data, ParamCtx ctx)
    {
        var parts = path.Split('.');

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].EndsWith("[]", StringComparison.Ordinal))
                return BuildPayloadArraySql(parts, i, data, ctx);
        }

        if (parts.Contains("*"))
        {
            var wildcardIdx = Array.IndexOf(parts, "*");
            string baseExpr;
            if (wildcardIdx == 0) baseExpr = "payload::jsonb";
            else
            {
                var sb = new StringBuilder("payload::jsonb");
                for (int i = 0; i < wildcardIdx; i++) sb.Append($"->'{EscapeSqlLiteral(parts[i])}'");
                baseExpr = sb.ToString();
            }
            return BuildWildcardTextSql($"({baseExpr})", data, ctx);
        }

        var arrowPath = string.Join("->", parts.Select(p => $"'{EscapeSqlLiteral(p)}'"));
        string textExtract;
        if (parts.Length > 1)
        {
            var nested = string.Join("->", parts[..^1].Select(p => $"'{EscapeSqlLiteral(p)}'"));
            textExtract = $"payload::jsonb->{nested}->>'{EscapeSqlLiteral(parts[^1])}'";
        }
        else textExtract = $"payload::jsonb->>'{EscapeSqlLiteral(parts[0])}'";

        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2) (v1, v2) = (v2, v1);
                var p1 = ctx.Add(v1);
                var p2 = ctx.Add(v2);
                return $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'number' AND (payload::jsonb->{arrowPath})::float {(data.Negative ? "NOT " : "")}BETWEEN CAST({p1} AS float) AND CAST({p2} AS float))";
            }
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            var sp1 = ctx.Add(v1);
            var sp2 = ctx.Add(v2);
            return $"({textExtract} {(data.Negative ? "NOT " : "")}BETWEEN {sp1} AND {sp2})";
        }

        return BuildPayloadValueSql(arrowPath, textExtract, parts, data, ctx);
    }

    private static string BuildPayloadContainmentJson(string[] parts, string jsonValueLiteral)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
            sb.Append('{').Append('"').Append(EscapeJsonStringLiteral(parts[i])).Append('"').Append(':');
        sb.Append(jsonValueLiteral);
        for (int i = 0; i < parts.Length; i++) sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeJsonStringLiteral(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? BuildPayloadArraySql(string[] parts, int arrayIdx, SearchField data, ParamCtx ctx)
    {
        var prefixParts = new List<string>(arrayIdx + 1);
        for (int i = 0; i < arrayIdx; i++) prefixParts.Add(parts[i]);
        prefixParts.Add(parts[arrayIdx][..^2]);
        var arrayPathArrow = string.Join("->", prefixParts.Select(p => $"'{EscapeSqlLiteral(p)}'"));
        var arrayExpr = $"payload::jsonb->{arrayPathArrow}";

        var remaining = parts.Skip(arrayIdx + 1).ToArray();
        bool hasSubPath = remaining.Length > 0;

        string elementText, elementJsonb, iterator;
        if (hasSubPath)
        {
            if (remaining.Length == 1)
            {
                elementText = $"x->>'{EscapeSqlLiteral(remaining[0])}'";
                elementJsonb = $"x->'{EscapeSqlLiteral(remaining[0])}'";
            }
            else
            {
                var nested = string.Join("->", remaining[..^1].Select(p => $"'{EscapeSqlLiteral(p)}'"));
                elementText = $"x->{nested}->>'{EscapeSqlLiteral(remaining[^1])}'";
                elementJsonb = $"x->{nested}->'{EscapeSqlLiteral(remaining[^1])}'";
            }
            iterator = $"jsonb_array_elements({arrayExpr}) AS x";
        }
        else
        {
            elementText = "e";
            elementJsonb = "e::jsonb";
            iterator = $"jsonb_array_elements_text({arrayExpr}) AS e";
        }

        var typeofGuard = $"jsonb_typeof({arrayExpr}) = 'array'";

        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2) (v1, v2) = (v2, v1);
                var p1 = ctx.Add(v1);
                var p2 = ctx.Add(v2);
                string between = hasSubPath
                    ? $"jsonb_typeof({elementJsonb}) = 'number' AND ({elementJsonb})::float BETWEEN CAST({p1} AS float) AND CAST({p2} AS float)"
                    : $"e::float BETWEEN CAST({p1} AS float) AND CAST({p2} AS float)";
                var exists = $"EXISTS (SELECT 1 FROM {iterator} WHERE {between})";
                return data.Negative ? $"({typeofGuard} AND NOT {exists})" : $"({typeofGuard} AND {exists})";
            }
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            var sp1 = ctx.Add(v1);
            var sp2 = ctx.Add(v2);
            var between2 = $"{elementText} BETWEEN {sp1} AND {sp2}";
            var exists2 = $"EXISTS (SELECT 1 FROM {iterator} WHERE {between2})";
            return data.Negative ? $"({typeofGuard} AND NOT {exists2})" : $"({typeofGuard} AND {exists2})";
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
                var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                predicate = hasSubPath
                    ? $"(jsonb_typeof({elementJsonb}) = 'number' AND ({elementJsonb})::float {sqlOp} CAST({pNum} AS float))"
                    : $"e::float {sqlOp} CAST({pNum} AS float)";
            }
            else if (data.Negative || compOp == "!")
            {
                var p = ctx.Add(value);
                predicate = $"{elementText} != {p}";
            }
            else if (isNum)
            {
                if (hasSubPath)
                {
                    var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    var pStr = ctx.Add(value);
                    predicate = $"((jsonb_typeof({elementJsonb}) = 'number' AND ({elementJsonb})::float = CAST({pNum} AS float)) OR {elementText} = {pStr})";
                }
                else
                {
                    var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    predicate = $"e::float = CAST({pNum} AS float)";
                }
            }
            else
            {
                var p = ctx.Add(value);
                predicate = $"{elementText} = {p}";
            }

            var exists = $"EXISTS (SELECT 1 FROM {iterator} WHERE {predicate})";
            conditions.Add(data.Negative ? $"({typeofGuard} AND NOT {exists})" : $"({typeofGuard} AND {exists})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    private static string? BuildPayloadValueSql(string arrowPath, string textExtract, string[] parts, SearchField data, ParamCtx ctx)
    {
        var conditions = new List<string>();
        var compOp = data.ComparisonOperator;

        // Null check: `@path:null` matches when the field is missing OR its
        // JSON value is null. Negated form (`-@path:null`) requires the
        // field to exist with a non-null value. Only fires for a lone
        // `null` token (case-insensitive), not when null is one of several
        // alternation values or part of a literal like `nullified`.
        if (!data.IsRange && compOp is null
            && data.Values.Count == 1
            && data.Values[0].Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            var pathExpr = $"payload::jsonb->{arrowPath}";
            return data.Negative
                ? $"({pathExpr} IS NOT NULL AND jsonb_typeof({pathExpr}) != 'null')"
                : $"({pathExpr} IS NULL OR jsonb_typeof({pathExpr}) = 'null')";
        }

        if (data.ValueType == "boolean")
        {
            foreach (var v in data.Values)
            {
                var bv = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                var p = ctx.Add(bv, NpgsqlDbType.Boolean);
                var eq = (data.Negative || compOp == "!") ? "!=" : "=";
                conditions.Add(
                    $"((jsonb_typeof(payload::jsonb->{arrowPath}) = 'boolean' AND ({textExtract})::boolean {eq} {p}) OR " +
                    $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'string' AND ({textExtract})::boolean {eq} {p}))");
            }
            return JoinConditions(conditions, data.Operation, data.Negative);
        }

        foreach (var value in data.Values)
        {
            bool isNum = NumericRegex.IsMatch(value);

            // Wildcard: `*` in value → ILIKE pattern on the textually-extracted
            // value, guarded by `jsonb_typeof = 'string'`. Supports prefix
            // (`foo*`), suffix (`*foo`), and contains (`*foo*`). Negative form
            // wraps the entire match in NOT so missing/non-string fields pass.
            // We DON'T enter this branch for a lone `*` (existence check is
            // handled upstream) or for ranges/comparison ops.
            if (compOp is null && value.Contains('*'))
            {
                var p = ctx.Add(value.Replace('*', '%'));
                var match = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'string' AND {textExtract} ILIKE {p})";
                conditions.Add(data.Negative ? $"(NOT {match})" : match);
                continue;
            }

            if (isNum && compOp is not null)
            {
                var sqlOp = compOp switch { "!" => "!=", ">" => ">", ">=" => ">=", "<" => "<", "<=" => "<=", _ => "=" };
                var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                conditions.Add(
                    $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'number' AND ({textExtract})::float {sqlOp} CAST({pNum} AS float))");
            }
            else if (data.Negative || compOp == "!")
            {
                var pVal = ctx.Add(value);
                var pJsonArr = ctx.Add(ToJsonArray(value), NpgsqlDbType.Jsonb);
                var arrayCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'array' AND NOT (payload::jsonb->{arrowPath} @> {pJsonArr}))";
                var stringCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'string' AND {textExtract} != {pVal})";
                if (isNum)
                {
                    var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    var numCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'number' AND ({textExtract})::float != CAST({pNum} AS float))";
                    conditions.Add($"({arrayCond} OR {stringCond} OR {numCond})");
                }
                else
                {
                    conditions.Add($"({arrayCond} OR {stringCond})");
                }
            }
            else
            {
                var pContainStr = ctx.Add(BuildPayloadContainmentJson(parts, ToJsonString(value)), NpgsqlDbType.Jsonb);
                var containStringCond = $"(payload::jsonb @> {pContainStr})";
                var pContainArr = ctx.Add(BuildPayloadContainmentJson(parts, ToJsonArray(value)), NpgsqlDbType.Jsonb);
                var containArrayCond = $"(payload::jsonb @> {pContainArr})";

                if (isNum)
                {
                    var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    var numCond = $"(jsonb_typeof(payload::jsonb->{arrowPath}) = 'number' AND ({textExtract})::float = CAST({pNum} AS float))";
                    conditions.Add($"({containStringCond} OR {containArrayCond} OR {numCond})");
                }
                else
                {
                    conditions.Add($"({containStringCond} OR {containArrayCond})");
                }
            }
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — JSONB array columns ————————————————————————————————————————————————

    private static string? BuildJsonbArraySql(string column, SearchField data, ParamCtx ctx)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var pVal = ctx.Add(value);
            var pJson = ctx.Add(ToJsonArray(value), NpgsqlDbType.Jsonb);
            if (data.Negative)
            {
                conditions.Add(
                    $"((jsonb_typeof({column}) = 'array' AND NOT ({column} @> {pJson})) OR " +
                    $"(jsonb_typeof({column}) = 'object' AND NOT ({column}::text ILIKE '%' || {pVal} || '%')))");
            }
            else
            {
                conditions.Add(
                    $"((jsonb_typeof({column}) = 'array' AND {column} @> {pJson}) OR " +
                    $"(jsonb_typeof({column}) = 'object' AND {column}::text ILIKE '%' || {pVal} || '%'))");
            }
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Text-array columns ————————————————————————————————————————————————

    private static string? BuildTextArraySql(string column, SearchField data, ParamCtx ctx)
    {
        var negative = data.Negative || data.ComparisonOperator == "!";
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            string predicate;
            if (value.Contains('*'))
            {
                var p = ctx.Add(value.Replace('*', '%'));
                predicate = $"elem ILIKE {p}";
            }
            else
            {
                var p = ctx.Add(value);
                predicate = $"elem = {p}";
            }
            var exists = $"EXISTS (SELECT 1 FROM unnest({column}) AS elem WHERE {predicate})";
            conditions.Add(negative ? $"NOT {exists}" : exists);
        }
        return JoinConditions(conditions, data.Operation, negative);
    }

    // — Wildcard text search on a JSONB subtree ————————————————————————————

    private static string? BuildWildcardTextSql(string baseExpr, SearchField data, ParamCtx ctx)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var p = ctx.Add($"%{value}%");
            conditions.Add(data.Negative
                ? $"({baseExpr}::text NOT ILIKE {p})"
                : $"({baseExpr}::text ILIKE {p})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Boolean column ————————————————————————————————————————————————

    private static string? BuildBooleanColumnSql(string column, SearchField data, ParamCtx ctx)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var bv = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            var p = ctx.Add(bv, NpgsqlDbType.Boolean);
            var eq = (data.Negative || data.ComparisonOperator == "!") ? "!=" : "=";
            conditions.Add($"(CAST({column} AS BOOLEAN) {eq} {p})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Timestamp column ——————————————————————————————————————————————

    private static string? BuildTimestampColumnSql(string column, SearchField data, ParamCtx ctx)
    {
        string ParamExpr(string v)
        {
            var p = ctx.Add(v);
            return NumericRegex.IsMatch(v) ? $"to_timestamp({p}::float8 / 1000.0)" : $"{p}::timestamptz";
        }

        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric"
                && double.TryParse(v1, NumberStyles.Float, CultureInfo.InvariantCulture, out var d1)
                && double.TryParse(v2, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)
                && d1 > d2)
            {
                (v1, v2) = (v2, v1);
            }
            else if (data.ValueType != "numeric" && string.Compare(v1, v2, StringComparison.Ordinal) > 0)
            {
                (v1, v2) = (v2, v1);
            }
            var p1 = ParamExpr(v1);
            var p2 = ParamExpr(v2);
            return $"({column} {(data.Negative ? "NOT " : "")}BETWEEN {p1} AND {p2})";
        }

        var conditions = new List<string>();
        var compOp = data.ComparisonOperator;
        foreach (var value in data.Values)
        {
            var pExpr = ParamExpr(value);
            if (compOp is not null && compOp != "!")
                conditions.Add(data.Negative ? $"NOT ({column} {compOp} {pExpr})" : $"{column} {compOp} {pExpr}");
            else if (data.Negative || compOp == "!")
                conditions.Add($"{column} != {pExpr}");
            else
                conditions.Add($"{column} = {pExpr}");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Scalar text/numeric column ————————————————————————————————————————

    private static string? BuildScalarSql(string fieldExpr, SearchField data, ParamCtx ctx)
    {
        var compOp = data.ComparisonOperator;
        var conditions = new List<string>();

        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2) (v1, v2) = (v2, v1);
                var p1 = ctx.Add(v1);
                var p2 = ctx.Add(v2);
                return $"(CAST({fieldExpr} AS FLOAT) {(data.Negative ? "NOT " : "")}BETWEEN CAST({p1} AS float) AND CAST({p2} AS float))";
            }
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            var sp1 = ctx.Add(v1);
            var sp2 = ctx.Add(v2);
            return $"({fieldExpr} {(data.Negative ? "NOT " : "")}BETWEEN {sp1} AND {sp2})";
        }

        foreach (var value in data.Values)
        {
            if (compOp is not null && compOp != "!")
            {
                var p = ctx.Add(value);
                var cast = "::numeric";
                conditions.Add(data.Negative
                    ? $"NOT ({fieldExpr}{cast} {compOp} {p}{cast})"
                    : $"{fieldExpr}{cast} {compOp} {p}{cast}");
            }
            else if (data.Negative || compOp == "!")
            {
                var p = ctx.Add(value);
                conditions.Add($"{fieldExpr} != {p}");
            }
            else
            {
                if (value.Contains('*'))
                {
                    var p = ctx.Add(value.Replace('*', '%'));
                    conditions.Add($"{fieldExpr} ILIKE {p}");
                }
                else
                {
                    var p = ctx.Add(value);
                    conditions.Add($"{fieldExpr} = {p}");
                }
            }
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — JSON literal helpers ——————————————————————————————————————————————

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

        string joinOp;
        if (negative) joinOp = operation == "AND" ? " OR " : " AND ";
        else joinOp = operation == "AND" ? " AND " : " OR ";

        return "(" + string.Join(joinOp, conditions) + ")";
    }

    private static string BuildJsonbPath(string column, string dotPath)
    {
        var segments = dotPath.Split('.');
        if (segments.Length == 0) return $"{column}::text";
        if (segments.Length == 1) return $"{column}::jsonb->>'{EscapeSqlLiteral(segments[0])}'";

        var sb = new StringBuilder($"{column}::jsonb");
        for (var i = 0; i < segments.Length - 1; i++)
            sb.Append($"->'{EscapeSqlLiteral(segments[i])}'");
        sb.Append($"->>'{EscapeSqlLiteral(segments[^1])}'");
        return sb.ToString();
    }
}
