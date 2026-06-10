using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

public class SemiJoinSqlTests
{
    [Theory]
    [InlineData("shortname", "entries.", "(entries.shortname)::text")]
    [InlineData("resource_type", "r.", "(r.resource_type)::text")]
    [InlineData("payload.body.customer", "entries.", "entries.payload::jsonb->'body'->>'customer'")]
    [InlineData("payload.body.customer", "r.", "r.payload::jsonb->'body'->>'customer'")]
    public void TryJoinKeyToSql_BuildsExpectedExpr(string path, string qualifier, string expected)
    {
        QueryHelper.TryJoinKeyToSql(path, qualifier, out var expr).ShouldBeTrue();
        expr.ShouldBe(expected);
    }

    [Theory]
    [InlineData("owner_shortname")]   // intentionally NOT a pushdown meta column
    [InlineData("relationships.x")]   // unsupported root
    [InlineData("payload.bad-seg")]   // hyphen fails segment validation
    public void TryJoinKeyToSql_RejectsUnsupported(string path)
    {
        QueryHelper.TryJoinKeyToSql(path, "r.", out _).ShouldBeFalse();
    }

    [Fact]
    public void AppendInnerSemiJoins_EmitsCorrelatedExists()
    {
        var args = new List<NpgsqlParameter> { new() { Value = "basespace" } }; // base already used $1
        var spec = new InnerSemiJoinSpec
        {
            RightQuery = new Query { Type = QueryType.Subpath, SpaceName = "rightspace", Subpath = "customers" },
            Actor = "dmart",
            RightQueryPolicies = null,
            Correlations = new() { ("entries.payload::jsonb->'body'->>'customer'", "(r.shortname)::text") },
        };
        var sql = new System.Text.StringBuilder();
        QueryHelper.AppendInnerSemiJoins(sql, args, new[] { spec });

        var text = sql.ToString();
        text.ShouldContain("AND EXISTS (SELECT 1 FROM entries r WHERE space_name = $2");
        text.ShouldContain("AND (r.shortname)::text = entries.payload::jsonb->'body'->>'customer'");
        text.TrimEnd().ShouldEndWith(")");
        // Right space param appended; ACL actor param appended (policies null → owner/acl only).
        args.Select(p => p.Value).ShouldContain("rightspace");
        args.Select(p => p.Value).ShouldContain("dmart");
    }
}
