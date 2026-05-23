using Dmart.SqlAdapter.Helpers;
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
    public void Parenthesised_Groups_OR_Together()
    {
        var parsed = SearchExpressionParser.Parse("(@shortname:alice) (@shortname:bob)", 0);

        // Two groups → top-level should contain " OR " between them.
        parsed.Clauses.Count.ShouldBe(1);
        parsed.Clauses[0].ShouldContain(" OR ");
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
        combined.ShouldContain("payload::jsonb->'body'->>'x' ILIKE @s_0");
        combined.ShouldContain("jsonb_typeof(payload::jsonb->'body'->'x') = 'string'");
        combined.ShouldNotContain("payload::jsonb @>");
        parsed.Parameters.Count.ShouldBe(1);
        parsed.Parameters[0].Value.ShouldBe("%delate%");
    }

    [Fact]
    public void Payload_Wildcard_Prefix_Emits_Trailing_Percent_Only()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.x:delate*", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("ILIKE @s_0");
        parsed.Parameters.Count.ShouldBe(1);
        parsed.Parameters[0].Value.ShouldBe("delate%");
    }

    [Fact]
    public void Payload_Wildcard_Suffix_Emits_Leading_Percent_Only()
    {
        var parsed = SearchExpressionParser.Parse("@payload.body.x:*delate", 0);

        var combined = string.Join(" ", parsed.Clauses);
        combined.ShouldContain("ILIKE @s_0");
        parsed.Parameters.Count.ShouldBe(1);
        parsed.Parameters[0].Value.ShouldBe("%delate");
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
        combined.ShouldContain("ILIKE @s_0");
        combined.ShouldContain("ILIKE @s_1");
        combined.ShouldContain(" OR ");
        parsed.Parameters.Count.ShouldBe(2);
        parsed.Parameters[0].Value.ShouldBe("%foo%");
        parsed.Parameters[1].Value.ShouldBe("bar%");
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
}
