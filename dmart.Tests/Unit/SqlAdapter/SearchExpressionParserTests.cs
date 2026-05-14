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
}
