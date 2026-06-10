using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

public class QueryHelperWhereTests
{
    private static Query Q(string space, string subpath) => new()
    {
        Type = QueryType.Subpath, SpaceName = space, Subpath = subpath,
    };

    [Fact]
    public void BuildWhereClause_BaseCase_BindsSpaceToDollar1()
    {
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(Q("myspace", "/"), args, "entries");
        where.ShouldStartWith("space_name = $1 ");
        args[0].Value.ShouldBe("myspace");
    }

    [Fact]
    public void BuildWhereClause_NestedCase_ContinuesPositionalParams()
    {
        // Simulate a base query that already consumed two params.
        var args = new List<NpgsqlParameter> { new() { Value = "x" }, new() { Value = "y" } };
        var where = QueryHelper.BuildWhereClause(Q("rightspace", "/"), args, "entries");
        where.ShouldStartWith("space_name = $3 ");      // not $1
        args.Count.ShouldBe(3);
        args[2].Value.ShouldBe("rightspace");
    }
}
