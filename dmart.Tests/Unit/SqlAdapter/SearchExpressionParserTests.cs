using Dmart.QueryGrammar;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.SqlAdapter;

// Pins SearchExpressionParser's emitted SQL fragments + parameter counts
// across the dialects it handles. Pure-logic tests — no DB. The goal is
// to surface behaviour drift as test diffs when the parser is touched.
//
// Each test asserts on a representative SUBSTRING of the emitted SQL plus
// the parameter count. We don't pin the FULL SQL because the exact spacing
// and parenthesisation is not part of the contract; what matters is the
// shape (which column / JSONB path is hit, which operator is emitted, the
// number of bound parameters).
public class SearchExpressionParserTests
{
    [Fact]
    public void Empty_Or_Whitespace_Returns_No_Clauses()
    {
        var parsed = SearchExpressionParser.Parse("", 0);
        parsed.Clauses.Count.ShouldBe(0);
        parsed.Parameters.Count.ShouldBe(0);

        var parsed2 = SearchExpressionParser.Parse("   ", 0);
        parsed2.Clauses.Count.ShouldBe(0);
    }

    [Fact]
    public void Plain_Field_Equals_Emits_Scalar_Equality_With_One_Param()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:alice", 0);

        parsed.Clauses.Count.ShouldBe(1);
        parsed.Clauses[0].ShouldContain("shortname::text = @s_0");
        parsed.Parameters.Count.ShouldBe(1);
        parsed.Parameters[0].ParameterName.ShouldBe("@s_0");
        parsed.Parameters[0].Value.ShouldBe("alice");
    }

    [Fact]
    public void Negation_Emits_Inequality()
    {
        var parsed = SearchExpressionParser.Parse("-@shortname:alice", 0);

        parsed.Clauses[0].ShouldContain("shortname::text != @s_0");
        parsed.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public void Alternation_Emits_OR_Group()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:alice|bob", 0);

        // Two values OR'd; two parameters.
        parsed.Parameters.Count.ShouldBe(2);
        parsed.Clauses[0].ShouldContain(" OR ");
    }

    [Fact]
    public void Numeric_Range_Emits_BETWEEN_Cast_To_Float()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:[1 10]", 0);

        parsed.Clauses[0].ShouldContain("BETWEEN CAST(@s_0 AS float) AND CAST(@s_1 AS float)");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void String_Range_Emits_BETWEEN_Without_Cast()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:[apple banana]", 0);

        parsed.Clauses[0].ShouldContain("BETWEEN @s_0 AND @s_1");
        parsed.Clauses[0].ShouldNotContain("AS float");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Comparison_Operator_On_Numeric_Emits_Cast_Comparison()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:>5", 0);

        // Scalar ">" path uses ::numeric on the field expr.
        parsed.Clauses[0].ShouldContain("> @s_0");
        parsed.Clauses[0].ShouldContain("::numeric");
        parsed.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public void Bang_Operator_Treated_As_Negation()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:!alice", 0);

        parsed.Clauses[0].ShouldContain("!= @s_0");
    }

    [Fact]
    public void Payload_Path_Builds_Jsonb_Containment()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.title:hello", 0);

        // Single-value containment emits at least one `payload::jsonb @> @s_*`
        // (plus an array-containment alternative).
        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb @>");
        combined.ShouldContain("@s_0");
    }

    [Fact]
    public void Payload_Array_Iterates_With_Jsonb_Array_Elements()
    {
        var parsed = SearchExpressionParser.Parse("@payload.items[].price:>5", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("jsonb_array_elements");
        combined.ShouldContain("> CAST(@s_0 AS float)");
    }

    [Fact]
    public void Jsonb_Array_Column_Tags_Uses_Containment_And_Object_Fallback()
    {
        var parsed = SearchExpressionParser.Parse("@tags:hot", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("jsonb_typeof(tags) = 'array'");
        combined.ShouldContain("tags @>");
    }

    [Fact]
    public void TextArray_Column_Query_Policies_Uses_Unnest()
    {
        var parsed = SearchExpressionParser.Parse("@query_policies:something", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("unnest(query_policies)");
    }

    [Fact]
    public void Boolean_Column_Casts_To_Boolean()
    {
        var parsed = SearchExpressionParser.Parse("@is_active:true", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("CAST(is_active AS BOOLEAN)");
    }

    [Fact]
    public void Timestamp_Range_Numeric_Wraps_In_To_Timestamp()
    {
        var parsed = SearchExpressionParser.Parse("@created_at:[1700000000 1800000000]", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("to_timestamp(@s_0::float8 / 1000.0)");
        combined.ShouldContain("to_timestamp(@s_1::float8 / 1000.0)");
    }

    [Fact]
    public void Free_Text_Term_Emits_ILIKE_Across_Default_Columns()
    {
        var parsed = SearchExpressionParser.Parse("foo", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname ILIKE @s_0");
        combined.ShouldContain("payload::text ILIKE @s_0");
        combined.ShouldContain("displayname::text ILIKE @s_0");
        combined.ShouldContain("tags::text ILIKE @s_0");
        // The free-text term gets %wrapped before bind.
        parsed.Parameters[0].Value.ShouldBe("%foo%");
    }

    [Fact]
    public void Parenthesised_Groups_AND_Together()
    {
        // BREAKING CHANGE (2026-06-20): whitespace between paren groups now
        // means AND, not OR. OR is expressed only via the `or` keyword or
        // value-level alternation `|`. Two single-field groups juxtaposed are
        // AND'd; neither field has an internal OR, so the only OR/AND that can
        // appear is the join between them.
        var parsed = SearchExpressionParser.Parse("(@shortname:alice) (@shortname:bob)", 0);

        parsed.Clauses.Count.ShouldBe(1);
        parsed.Clauses[0].ShouldContain(" AND ");
        parsed.Clauses[0].ShouldNotContain(" OR ");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void StartingParamIndex_Offsets_Placeholder_Names()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:alice", startingParamIndex: 7);

        parsed.Parameters[0].ParameterName.ShouldBe("@s_7");
        parsed.Clauses[0].ShouldContain("@s_7");
    }

    [Fact]
    public void User_Meta_Column_Email_Joins_Through_Owner()
    {
        var parsed = SearchExpressionParser.Parse("@email:alice@example.com", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("owner_shortname IN (SELECT shortname FROM users WHERE email = @s_0)");
    }

    [Fact]
    public void User_Meta_Column_Email_Skips_Join_When_TargetTable_Is_Users()
    {
        // When the query is itself against `users`, joining through
        // owner_shortname → users would reference a column that doesn't
        // exist on the target table (users has shortname, not
        // owner_shortname). The parser must fall through to the scalar
        // column path instead.
        var parsed = SearchExpressionParser.Parse(
            "@email:alice@example.com",
            startingParamIndex: 0,
            style: PlaceholderStyle.Named,
            targetTable: "users");

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldNotContain("owner_shortname");
        combined.ShouldNotContain("SELECT shortname FROM users");
        // The scalar-column path emits `email::text = @s_0` (or similar).
        combined.ShouldContain("email");
        combined.ShouldContain("@s_0");
    }

    [Fact]
    public void User_Meta_Column_Msisdn_Skips_Join_When_TargetTable_Is_Users()
    {
        var parsed = SearchExpressionParser.Parse(
            "@msisdn:0096170123456",
            startingParamIndex: 0,
            style: PlaceholderStyle.Named,
            targetTable: "users");

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldNotContain("owner_shortname");
        combined.ShouldContain("msisdn");
    }

    // ── Null search on payload paths ──────────────────────────────────────
    // `@payload.body.x:null` matches rows where the JSONB path resolves to
    // SQL NULL (missing key) OR JSON null (`jsonb_typeof = 'null'`). The
    // string "null" literal is NOT supposed to land in the @> containment
    // path — both branches share that requirement.

    [Fact]
    public void Payload_Null_Search_Emits_IsNull_Or_JsonbNull_Typeof()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.x:null", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb->'body'->'x' IS NULL");
        combined.ShouldContain("jsonb_typeof(payload::jsonb->'body'->'x') = 'null'");
        // No containment / no extracted-text scanning — pure path-nullness check.
        combined.ShouldNotContain("payload::jsonb @>");
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void Payload_Null_Search_Negated_Requires_NonNull_And_NotJsonbNull()
    {
        var parsed = SearchExpressionParser.Parse("-@payload.body.x:null", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb->'body'->'x' IS NOT NULL");
        combined.ShouldContain("jsonb_typeof(payload::jsonb->'body'->'x') != 'null'");
        parsed.Parameters.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("Null")]
    [InlineData("NuLl")]
    public void Payload_Null_Search_Case_Insensitive(string literal)
    {
        var parsed = SearchExpressionParser.Parse($"@payload.body.x:{literal}", 0);
        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("IS NULL");
        combined.ShouldContain("jsonb_typeof");
        parsed.Parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void Payload_String_Nullified_Is_NOT_Treated_As_Null()
    {
        // "nullified" is a real string value, not a null sentinel — must
        // still go through the regular containment path.
        var parsed = SearchExpressionParser.Parse("@payload.body.x:nullified", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb @>");
        combined.ShouldNotContain("IS NULL");
        parsed.Parameters.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Payload_Null_Inside_Alternation_Treated_As_Literal()
    {
        // `null|other` is two values; we deliberately don't try to be clever
        // and split the null branch — fall through to the standard containment
        // path so behavior matches the existing alternation semantics.
        var parsed = SearchExpressionParser.Parse("@payload.body.x:null|other", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldNotContain("IS NULL");
        combined.ShouldContain("@>");
    }

    [Fact]
    public void Payload_Null_With_Deep_Path_Builds_Full_Arrow_Chain()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.address.city:null", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb->'body'->'address'->'city' IS NULL");
        combined.ShouldContain("jsonb_typeof(payload::jsonb->'body'->'address'->'city') = 'null'");
    }

    [Fact]
    public void Payload_Null_With_Single_Segment_Path_Works()
    {
        // No nested body — just `payload.field:null`.
        var parsed = SearchExpressionParser.Parse("@payload.foo:null", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb->'foo' IS NULL");
    }

    // ── Wildcard search on payload paths ──────────────────────────────────
    // `@payload.body.x:*foo*` / `foo*` / `*foo` need to compile to ILIKE
    // on the textually-extracted JSONB scalar, NOT to JSON containment
    // (which is an exact-match operator and would never hit any rows).

    [Fact]
    public void Payload_Wildcard_Contains_Emits_ILIKE_Pattern_With_Percent_On_Both_Sides()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.x:*delate*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        // Per-path filter uses the user's wildcard shape.
        combined.ShouldContain("payload::jsonb->'body'->>'x' ILIKE @s_0");
        combined.ShouldContain("jsonb_typeof(payload::jsonb->'body'->'x') = 'string'");
        // pg_trgm prefilter is a separate param so it can be
        // contains-form regardless of the per-path pattern.
        combined.ShouldContain("payload::text ILIKE @s_1");
        combined.ShouldNotContain("payload::jsonb @>");
        parsed.Parameters.Count.ShouldBe(2);
        parsed.Parameters[0].Value.ShouldBe("%delate%");  // per-path
        parsed.Parameters[1].Value.ShouldBe("%delate%");  // prefilter (same here)
    }

    [Fact]
    public void Payload_Wildcard_Prefix_Emits_Trailing_Percent_Only()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.x:delate*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("ILIKE @s_0");
        combined.ShouldContain("payload::text ILIKE @s_1");
        // Prefilter is contains-form even though per-path is prefix-form.
        // Without this, `payload::text ILIKE 'delate%'` would require
        // the payload to START with "delate" — but it starts with `{`,
        // so nothing matches.
        parsed.Parameters.Count.ShouldBe(2);
        parsed.Parameters[0].Value.ShouldBe("delate%");   // per-path (prefix)
        parsed.Parameters[1].Value.ShouldBe("%delate%");  // prefilter (contains)
    }

    [Fact]
    public void Payload_Wildcard_Suffix_Emits_Leading_Percent_Only()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.x:*delate", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("ILIKE @s_0");
        combined.ShouldContain("payload::text ILIKE @s_1");
        // Same reasoning — prefilter contains-form so the GIN serves
        // the lookup regardless of the per-path wildcard position.
        parsed.Parameters.Count.ShouldBe(2);
        parsed.Parameters[0].Value.ShouldBe("%delate");   // per-path (suffix)
        parsed.Parameters[1].Value.ShouldBe("%delate%");  // prefilter (contains)
    }

    [Fact]
    public void Payload_Wildcard_Negated_Does_Not_Emit_Trgm_Prefilter()
    {
        // The negated branch is "exclude rows containing X" — the trigram
        // index tells us which rows DO contain X (the opposite of what we
        // want). Adding the prefilter to the negated form would only
        // exclude rows that match — which is what NOT ILIKE already does
        // at the per-path level — so prepending the prefilter does no
        // useful work. Verify it's NOT emitted; planner picks the most
        // selective access path on its own.
        var parsed = SearchExpressionParser.Parse("-@payload.body.x:*delate*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("NOT ILIKE @s_0");
        // Exactly one ILIKE clause — the per-path one. If the prefilter
        // had been emitted there'd be a second `payload::text ILIKE`.
        // Counting via OccurrenceCount on the full string is the cleanest
        // way to assert "exactly one." A naive Contains-check passes even
        // when both fire.
        var ilikeCount = System.Text.RegularExpressions.Regex
            .Matches(combined, @"\bILIKE\b").Count;
        ilikeCount.ShouldBe(1,
            $"negated wildcard should emit one ILIKE (per-path), got {ilikeCount} in: {combined}");
    }

    [Fact]
    public void Payload_Wildcard_Negated_Allows_Missing_Or_NonString_Fields()
    {
        // -@k:*foo* should let rows pass when the field is missing,
        // non-string, or a string that doesn't contain "foo". A direct
        // `NOT (typeof='string' AND ILIKE pattern)` is wrong: when the
        // field is absent both inner operands are NULL, NOT-NULL is NULL,
        // and WHERE drops the row. We spell out the three passing cases
        // (IS NULL, non-string, NOT ILIKE) so all of them evaluate cleanly
        // to TRUE under three-valued logic.
        var parsed = SearchExpressionParser.Parse("-@payload.body.x:*delate*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb->'body'->'x' IS NULL");
        combined.ShouldContain("IS DISTINCT FROM 'string'");
        combined.ShouldContain("NOT ILIKE @s_0");
        parsed.Parameters[0].Value.ShouldBe("%delate%");
    }

    [Fact]
    public void Payload_Wildcard_Escapes_Literal_Percent_Underscore_Backslash()
    {
        // PG's default LIKE metachars (%, _, \) must be escaped BEFORE we
        // swap `*` → `%`, otherwise user-supplied literals act as wildcards
        // or escape sequences. Worst-case prior behavior: `code:100%off*`
        // matched every record with a string `code` field because `%` was
        // an unescaped wildcard.
        var parsed = SearchExpressionParser.Parse("@payload.body.code:100%off_v\\*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("ILIKE @s_0");
        // `\` → `\\`, `%` → `\%`, `_` → `\_`, `*` → `%`.
        parsed.Parameters[0].Value.ShouldBe(@"100\%off\_v\\%");
    }

    [Fact]
    public void Payload_Wildcard_Lone_Star_In_Alternation_Falls_Through_To_Containment()
    {
        // `@k:vip|*` previously hit the wildcard branch on the `*` value
        // and emitted `ILIKE '%'` — matching every string-typed row. With
        // the `value.Length > 1` guard the lone `*` falls through to the
        // normal containment path for the literal string "*", consistent
        // with the rest of the alternation grammar.
        var parsed = SearchExpressionParser.Parse("@payload.body.x:vip|*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        // `vip` builds a containment literal; `*` likewise (as the literal
        // single-char string) — neither emits ILIKE.
        combined.ShouldNotContain("ILIKE");
        combined.ShouldContain("@>");
    }

    [Fact]
    public void Payload_Wildcard_With_Alternation_Emits_OR_Of_Two_Patterns()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.x:*foo*|bar*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        // Both alternates produce ILIKE patterns joined by " OR ".
        // Each positive wildcard binds two params: per-path + prefilter.
        combined.ShouldContain("ILIKE @s_0");
        combined.ShouldContain("ILIKE @s_1");
        combined.ShouldContain("ILIKE @s_2");
        combined.ShouldContain("ILIKE @s_3");
        combined.ShouldContain(" OR ");
        parsed.Parameters.Count.ShouldBe(4);
        parsed.Parameters[0].Value.ShouldBe("%foo%");   // *foo* per-path
        parsed.Parameters[1].Value.ShouldBe("%foo%");   // *foo* prefilter
        parsed.Parameters[2].Value.ShouldBe("bar%");    // bar* per-path
        parsed.Parameters[3].Value.ShouldBe("%bar%");   // bar* prefilter
    }

    [Fact]
    public void Payload_Wildcard_Mixed_Alternation_Plain_And_Wildcard_Coexist()
    {
        // `@k:foo|*bar*|baz` — only the middle alternate is a wildcard.
        // The per-value loop should emit JSONB containment for `foo` and
        // `baz` and ILIKE for `*bar*`, OR'd together. If a regression
        // makes wildcard handling all-or-nothing at the alternation level
        // (e.g. one wildcard forces every alternate to ILIKE, or vice
        // versa), this catches it. Each containment value contributes two
        // params (string + array containment JSON); the ILIKE adds one
        // more, so the wildcard parameter lives somewhere in the middle
        // of the list rather than at a fixed index.
        var parsed = SearchExpressionParser.Parse("@payload.body.x:foo|*bar*|baz", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("ILIKE");
        combined.ShouldContain("@>");
        combined.ShouldContain(" OR ");
        parsed.Parameters.Any(p => Equals(p.Value, "%bar%")).ShouldBeTrue(
            "expected an ILIKE-pattern parameter for the *bar* alternate");
    }

    [Fact]
    public void Payload_Wildcard_With_Deep_Path_Targets_Correct_Field()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.address.city:*ville*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb->'body'->'address'->>'city' ILIKE @s_0");
        combined.ShouldContain("jsonb_typeof(payload::jsonb->'body'->'address'->'city') = 'string'");
        parsed.Parameters[0].Value.ShouldBe("%ville%");
    }

    [Fact]
    public void Payload_Lone_Star_Is_Still_Existence_Check_Not_Wildcard()
    {
        // `@payload.body.x:*` continues to mean "field exists" and stays
        // on the existing IS NOT NULL branch — the wildcard work didn't
        // hijack the existence sentinel.
        var parsed = SearchExpressionParser.Parse("@payload.body.x:*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb->'body'->'x' IS NOT NULL");
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void Payload_NonWildcard_Still_Uses_Containment_For_Index_Hit()
    {
        // Sanity-guard the original fast path: a plain value WITHOUT `*` must
        // continue to compile to `payload @> jsonb_literal` so the GIN index
        // can serve it. Adding wildcard support shouldn't regress that.
        var parsed = SearchExpressionParser.Parse("@payload.body.x:hello", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb @>");
        combined.ShouldNotContain("ILIKE");
    }

    [Fact]
    public void Quoted_Value_With_Parens_Not_Treated_As_Group_Delimiters()
    {
        // Parens inside a quoted field value are part of the literal and must
        // not be treated as group delimiters or have spaces injected around them.
        var parsed = SearchExpressionParser.Parse(
            "@displayname.en:\"*Poco Pad M1 8/256GB(Blue)*\"", 0);

        parsed.Clauses.Count.ShouldBe(1);
        // Pattern must preserve the parens without surrounding spaces.
        parsed.Parameters.Count.ShouldBe(1);
        var pattern = parsed.Parameters[0].Value as string;
        pattern.ShouldNotBeNull();
        pattern.ShouldContain("(Blue)");
        pattern.ShouldNotContain("( Blue )");
    }

    [Fact]
    public void Grouping_Parens_And_Quoted_Parens_Coexist()
    {
        // A genuine group delimiter AND a quoted field value containing parens in
        // the same expression: the group still forms while the quoted value's
        // parens survive unspaced. This exercises the normalize path (not the
        // no-paren fast path the test above hits).
        //
        // BREAKING CHANGE (2026-06-20): `(A) B` is now AND, not OR — neither
        // operand has an internal OR, so the join is a bare AND.
        var parsed = SearchExpressionParser.Parse(
            "(@shortname:alice) @displayname.en:\"*x(Blue)y*\"", 0);

        parsed.Clauses.Count.ShouldBe(1);
        parsed.Clauses[0].ShouldContain(" AND ");  // group AND'd with the next selector
        parsed.Clauses[0].ShouldNotContain(" OR ");

        var pattern = parsed.Parameters
            .Select(p => p.Value as string)
            .FirstOrDefault(v => v is not null && v.Contains("Blue"));
        pattern.ShouldNotBeNull();
        pattern.ShouldContain("(Blue)");
        pattern.ShouldNotContain("( Blue )");
    }

    [Fact]
    public void Stray_Quote_In_Text_Does_Not_Suppress_Following_Group()
    {
        // A lone '"' in free text (here a 5" measurement) must not open a quoted
        // span that swallows the group delimiters after it — a quote only opens a
        // value when it directly follows an `@field:` prefix. The (@shortname:bob)
        // group must still parse as a real field filter, not inert quoted text.
        var parsed = SearchExpressionParser.Parse("size 5\" (@shortname:bob)", 0);

        parsed.Clauses.Count.ShouldBe(1);
        parsed.Clauses[0].ShouldContain("shortname");
        parsed.Parameters.Any(p => (p.Value as string) == "bob").ShouldBeTrue();
    }

    // ── `or` boolean keyword ──────────────────────────────────────────────
    // Added 2026-06-20. `or` (case-insensitive, standalone token) introduces
    // disjunction between adjacent terms/groups. Whitespace remains AND; AND
    // binds tighter than OR. The keyword is recognized ONLY as a bare token —
    // never inside a field value. See
    // docs/superpowers/specs/2026-06-20-query-search-or-keyword-design.md.

    // Helper: count non-overlapping occurrences of a separator in a string.
    private static int Occurrences(string haystack, string needle)
        => System.Text.RegularExpressions.Regex
            .Matches(haystack, System.Text.RegularExpressions.Regex.Escape(needle)).Count;

    [Fact]
    public void Or_Keyword_Between_Two_Selectors_Emits_Top_Level_Or()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:alice or @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain(" OR ");
        // `or` must NOT be treated as a free-text term (which would emit an
        // ILIKE for "%or%") — this is the core regression vs the old parser.
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(2);
        parsed.Parameters[0].Value.ShouldBe("alice");
        parsed.Parameters[1].Value.ShouldBe("bob");
    }

    [Theory]
    [InlineData("or")]
    [InlineData("OR")]
    [InlineData("Or")]
    [InlineData("oR")]
    public void Or_Keyword_Is_Case_Insensitive(string kw)
    {
        var parsed = SearchExpressionParser.Parse($"@shortname:alice {kw} @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain(" OR ");
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void And_Binds_Tighter_Than_Or()
    {
        // a b or c d  ⇒  (a AND b) OR (c AND d).
        // Use scalar text columns (each emits `field::text = @s_n`, no internal
        // OR/AND) so the only OR/AND in the output is structural.
        var parsed = SearchExpressionParser.Parse("@aa:1 @bb:2 or @cc:3 @dd:4", 0);

        var combined = string.Join(" ", parsed.Clauses);
        // Exactly one structural OR (between the two AND-groups) and two ANDs.
        Occurrences(combined, " OR ").ShouldBe(1);
        Occurrences(combined, " AND ").ShouldBe(2);
        // Left of the OR are aa/bb; right are cc/dd.
        var orIdx = combined.IndexOf(" OR ", System.StringComparison.Ordinal);
        var left = combined[..orIdx];
        var right = combined[orIdx..];
        left.ShouldContain("aa::text");
        left.ShouldContain("bb::text");
        right.ShouldContain("cc::text");
        right.ShouldContain("dd::text");
        parsed.Parameters.Count.ShouldBe(4);
    }

    [Fact]
    public void Parens_Override_Precedence_Or_Group_Anded_With_Term()
    {
        // (x or y) z  ⇒  (x OR y) AND z. The user's headline example shape.
        var parsed = SearchExpressionParser.Parse("(@xx:1 or @yy:2) @zz:3", 0);

        var combined = string.Join(" ", parsed.Clauses);
        Occurrences(combined, " OR ").ShouldBe(1);
        Occurrences(combined, " AND ").ShouldBe(1);
        // The OR (between x and y) is nested inside; the AND joins that group
        // with z. So the OR appears textually before the AND.
        var orIdx = combined.IndexOf(" OR ", System.StringComparison.Ordinal);
        var andIdx = combined.IndexOf(" AND ", System.StringComparison.Ordinal);
        orIdx.ShouldBeLessThan(andIdx);
        // z is on the AND side, outside the OR group.
        combined.ShouldContain("zz::text");
        parsed.Parameters.Count.ShouldBe(3);
    }

    [Fact]
    public void Trailing_Or_Is_Dropped_Reduces_To_Left()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:alice or", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text = @s_0");
        combined.ShouldNotContain(" OR ");
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public void Leading_Or_Is_Dropped_Reduces_To_Right()
    {
        var parsed = SearchExpressionParser.Parse("or @shortname:alice", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text = @s_0");
        combined.ShouldNotContain(" OR ");
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public void Double_Or_Drops_Empty_Operand()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:alice or or @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        Occurrences(combined, " OR ").ShouldBe(1);
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Empty_Group_Alone_Yields_No_Clauses()
    {
        var parsed = SearchExpressionParser.Parse("()", 0);

        parsed.Clauses.Count.ShouldBe(0);
        parsed.Parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void Or_With_Empty_Group_Reduces_To_Other_Side()
    {
        var parsed = SearchExpressionParser.Parse("(@shortname:alice) or ()", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text = @s_0");
        combined.ShouldNotContain(" OR ");
        parsed.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public void Empty_Group_On_Left_Of_Or_Reduces_To_Right()
    {
        var parsed = SearchExpressionParser.Parse("() or @shortname:alice", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text = @s_0");
        combined.ShouldNotContain(" OR ");
        parsed.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public void Unclosed_Paren_Is_Auto_Closed()
    {
        var parsed = SearchExpressionParser.Parse("(@shortname:alice or @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain(" OR ");
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Stray_Close_Paren_Is_Ignored_And_Terms_And_Together()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:alice) @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        // Stray ')' dropped; the two selectors AND together (whitespace = AND).
        combined.ShouldContain(" AND ");
        combined.ShouldNotContain(" OR ");
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Stray_Close_Paren_Before_Or_Preserves_Or()
    {
        // A stray ')' must be ignored WITHOUT changing the boolean meaning of a
        // following `or`. (Regression guard: a buggy recovery path once dropped
        // the `or` and silently AND'd the operands instead.)
        var parsed = SearchExpressionParser.Parse("@shortname:alice) or @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain(" OR ");
        combined.ShouldNotContain(" AND ");
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Over_Closed_Group_Before_Or_Preserves_Or()
    {
        // Realistic typo: an extra ')' after a balanced group, then `or`.
        var parsed = SearchExpressionParser.Parse("(@shortname:alice)) or @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain(" OR ");
        combined.ShouldNotContain(" AND ");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Free_Text_Term_Can_Be_An_Or_Operand()
    {
        var parsed = SearchExpressionParser.Parse("foo or @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain(" OR ");
        // Left branch is the free-text fan-out (ILIKE); right is the selector.
        combined.ShouldContain("shortname ILIKE @s_0");
        combined.ShouldContain("shortname::text = @s_1");
        parsed.Parameters[0].Value.ShouldBe("%foo%");
        parsed.Parameters[1].Value.ShouldBe("bob");
    }

    [Fact]
    public void Nested_Groups_Respect_Structure()
    {
        // ((a or b) c) or d  ⇒  (((a OR b) AND c) OR d).
        var parsed = SearchExpressionParser.Parse("((@aa:1 or @bb:2) @cc:3) or @dd:4", 0);

        var combined = string.Join(" ", parsed.Clauses);
        Occurrences(combined, " OR ").ShouldBe(2); // (a OR b) and (... OR d)
        Occurrences(combined, " AND ").ShouldBe(1); // (... AND c)
        combined.ShouldNotContain("ILIKE");
        parsed.Parameters.Count.ShouldBe(4);

        // Structure (not just counts): cc is AND'd onto the (aa OR bb) group, and
        // dd is the right operand of the OUTER, last OR. Counts alone can't tell
        // `(((a OR b) AND c) OR d)` apart from `((a OR b) OR (c AND d))`.
        var lastOr = combined.LastIndexOf(" OR ", System.StringComparison.Ordinal);
        var andIdx = combined.IndexOf(" AND ", System.StringComparison.Ordinal);
        andIdx.ShouldBeLessThan(lastOr);                 // the AND is inside the left operand of the outer OR
        combined.IndexOf("aa::text", System.StringComparison.Ordinal).ShouldBeLessThan(andIdx);
        combined.IndexOf("bb::text", System.StringComparison.Ordinal).ShouldBeLessThan(andIdx);
        combined.IndexOf("cc::text", System.StringComparison.Ordinal).ShouldBeLessThan(lastOr); // c is left of the outer OR
        combined.IndexOf("dd::text", System.StringComparison.Ordinal).ShouldBeGreaterThan(lastOr); // d is the outer-OR right operand
    }

    [Fact]
    public void Three_Way_Or_Flattens_To_Single_Or_Level()
    {
        // N-ary OR (3 operands) flattens into one OrNode → `(a OR b OR c)`,
        // not pairwise-nested. Counts: two " OR " separators, three params.
        var parsed = SearchExpressionParser.Parse("@aa:1 or @bb:2 or @cc:3", 0);

        var combined = string.Join(" ", parsed.Clauses);
        Occurrences(combined, " OR ").ShouldBe(2);
        Occurrences(combined, " AND ").ShouldBe(0);
        parsed.Parameters.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("and and")]
    [InlineData("(   )")]
    public void Bare_Operators_And_Empty_Groups_Yield_No_Clauses(string input)
    {
        // Lenient contract: operator-only / empty-group-only input never throws
        // and produces no clause (same as empty input).
        var parsed = SearchExpressionParser.Parse(input, 0);

        parsed.Clauses.Count.ShouldBe(0);
        parsed.Parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void Or_As_Field_Value_Is_Literal_Not_Keyword()
    {
        var parsed = SearchExpressionParser.Parse("@shortname:or", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text = @s_0");
        combined.ShouldNotContain(" OR ");
        parsed.Parameters.Count.ShouldBe(1);
        parsed.Parameters[0].Value.ShouldBe("or");
    }

    [Fact]
    public void Quoted_Value_Containing_Or_Keyword_Is_Literal()
    {
        // A quoted value is one token (the regex's `@…:"…"` alternative), so a
        // whitespace-delimited `or` *inside* the quotes is part of the value,
        // not the boolean keyword.
        var parsed = SearchExpressionParser.Parse("@shortname:\"alice or bob\"", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text = @s_0");
        combined.ShouldNotContain(" OR ");
        parsed.Parameters.Count.ShouldBe(1);
        parsed.Parameters[0].Value.ShouldBe("alice or bob");
    }

    [Fact]
    public void And_Keyword_Between_Groups_Is_A_No_Op_And()
    {
        // The literal `and` between paren groups is the optional no-op synonym
        // for whitespace — both mean AND. (`(A) and (B)` == `(A) (B)`.)
        var withKeyword = SearchExpressionParser.Parse("(@is_active:true) and (@is_open:false)", 0);
        var withSpace = SearchExpressionParser.Parse("(@is_active:true) (@is_open:false)", 0);

        var a = string.Join(" ", withKeyword.Clauses);
        var b = string.Join(" ", withSpace.Clauses);
        a.ShouldBe(b);
        a.ShouldContain(" AND ");
        a.ShouldNotContain(" OR ");
    }

    [Fact]
    public void Value_Containing_Or_Substring_Is_Literal()
    {
        // "author" contains "or" but is a single token → literal value.
        var parsed = SearchExpressionParser.Parse("@shortname:author", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text = @s_0");
        combined.ShouldNotContain(" OR ");
        parsed.Parameters[0].Value.ShouldBe("author");
    }

    [Fact]
    public void Alternation_Composes_With_Or_Keyword()
    {
        // `@k:a|b or @k:c` — alternation OR inside the left selector, plus the
        // keyword OR joining it to the right selector. Two distinct OR sources.
        var parsed = SearchExpressionParser.Parse("@shortname:a|b or @shortname:c", 0);

        var combined = string.Join(" ", parsed.Clauses);
        Occurrences(combined, " OR ").ShouldBeGreaterThanOrEqualTo(2);
        parsed.Parameters.Count.ShouldBe(3);
    }

    [Fact]
    public void Negation_Within_An_Or_Branch_Is_Preserved()
    {
        var parsed = SearchExpressionParser.Parse("-@shortname:alice or @shortname:bob", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text != @s_0");
        combined.ShouldContain("shortname::text = @s_1");
        combined.ShouldContain(" OR ");
        parsed.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Payload_Selectors_Compose_With_Or_Keyword()
    {
        // End-to-end: real payload containment on both sides, OR'd.
        var parsed = SearchExpressionParser.Parse("@payload.body.a:1 or @payload.body.b:2", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("payload::jsonb @>");
        combined.ShouldContain(" OR ");
        // Both payload paths are present.
        combined.ShouldContain("'a'");
        combined.ShouldContain("'b'");
    }

    // ── Regression guards: leaf-run semantics survive the rewrite ──────────

    [Fact]
    public void Same_Field_Accumulation_Preserved_Within_Leaf_Run()
    {
        // `@tags:a @tags:b` (juxtaposition, same sign) accumulates into a
        // single field with AND semantics — both tags must be present. This
        // must NOT regress to OR or to two separate groups.
        var parsed = SearchExpressionParser.Parse("@tags:a @tags:b", 0);

        var combined = string.Join(" ", parsed.Clauses);
        Occurrences(combined, "tags @>").ShouldBe(2);
        combined.ShouldContain(" AND ");
        // Each tags value binds two params (text + jsonb-array literal).
        parsed.Parameters.Count.ShouldBe(4);
    }

    [Fact]
    public void Or_Boundary_Scopes_Accumulation_Into_Separate_Groups()
    {
        // `@tags:a or @tags:b` is OR of two independent single-value groups,
        // NOT accumulation — the `or` boundary starts a fresh leaf.
        var parsed = SearchExpressionParser.Parse("@tags:a or @tags:b", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain(" OR ");
        Occurrences(combined, "tags @>").ShouldBe(2);
        parsed.Parameters.Count.ShouldBe(4);
    }

    [Fact]
    public void Last_Sign_Wins_Preserved_Within_Leaf_Run()
    {
        // `@shortname:x -@shortname:x` — opposite signs on the same field in
        // one run; the last sign wins → negation. Preserved across the rewrite.
        var parsed = SearchExpressionParser.Parse("@shortname:dup -@shortname:dup", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("shortname::text != @s_0");
        combined.ShouldNotContain("shortname::text = @s_0");
        parsed.Parameters.Count.ShouldBe(1);
    }
}
