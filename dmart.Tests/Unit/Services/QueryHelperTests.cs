using System.Collections.Generic;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for QueryHelper's search parsing and ACL clause generation —
// pure SQL-building logic, no DB required.
public class QueryHelperTests
{
    // ==================== RediSearch @field:value parsing ====================

    [Fact]
    public void Search_PlainText_Generates_ILIKE_On_Common_Fields()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/", Search = "hello" };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain("ILIKE");
        where.ShouldContain("payload::text");
        where.ShouldContain("displayname::text");
    }

    [Fact]
    public void Search_FieldValue_Generates_Jsonb_Path()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/",
            Search = "@payload.body.email:test@example.com" };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        // Should generate a JSONB path accessor for payload->'body'->>'email'
        where.ShouldContain("payload::jsonb");
        where.ShouldContain("ILIKE");
    }

    [Fact]
    public void Search_Negation_Generates_NOT()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/",
            Search = "-@status:deleted" };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain("NOT ILIKE");
    }

    [Fact]
    public void Search_Comparison_Operator_Generates_Numeric_Compare()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/",
            Search = "@payload.body.price:>100" };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain(">");
        where.ShouldContain("::numeric");
    }

    [Fact]
    public void Search_OrPipe_Generates_OR_Clauses()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/",
            Search = "@status:active|pending" };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain(" OR ");
    }

    [Fact]
    public void Search_MultipleFields_Generates_AND_Clauses()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/",
            Search = "@name:john @email:test" };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain(" AND ");
    }

    // ==================== ACL filtering ====================

    [Fact]
    public void AclFilter_Adds_Owner_And_Acl_Conditions()
    {
        var sql = new System.Text.StringBuilder("WHERE space_name = $1 ");
        var args = new List<NpgsqlParameter> { new() { Value = "test" } };
        QueryHelper.AppendAclFilter(sql, args, "alice", "entries", null);
        var result = sql.ToString();
        result.ShouldContain("owner_shortname =");
        result.ShouldContain("jsonb_array_elements");
        result.ShouldContain("'query'");
    }

    [Fact]
    public void AclFilter_With_QueryPolicies_Adds_LIKE_Patterns()
    {
        var sql = new System.Text.StringBuilder("WHERE space_name = $1 ");
        var args = new List<NpgsqlParameter> { new() { Value = "test" } };
        var policies = new List<string> { "test:api:content:true:*", "test:api:content:*" };
        QueryHelper.AppendAclFilter(sql, args, "alice", "entries", policies);
        var result = sql.ToString();
        result.ShouldContain("unnest(query_policies)");
        result.ShouldContain("LIKE");
    }

    [Fact]
    public void AclFilter_Skips_Attachments_And_Histories()
    {
        var sql = new System.Text.StringBuilder("WHERE space_name = $1 ");
        var args = new List<NpgsqlParameter> { new() { Value = "test" } };
        QueryHelper.AppendAclFilter(sql, args, "alice", "attachments", null);
        sql.ToString().ShouldNotContain("owner_shortname");

        sql = new System.Text.StringBuilder("WHERE space_name = $1 ");
        args = new List<NpgsqlParameter> { new() { Value = "test" } };
        QueryHelper.AppendAclFilter(sql, args, "alice", "histories", null);
        sql.ToString().ShouldNotContain("owner_shortname");
    }

    [Fact]
    public void AclFilter_Skips_When_No_User()
    {
        var sql = new System.Text.StringBuilder("WHERE space_name = $1 ");
        var args = new List<NpgsqlParameter> { new() { Value = "test" } };
        QueryHelper.AppendAclFilter(sql, args, null, "entries", null);
        sql.ToString().ShouldNotContain("owner_shortname");
    }

    // ==================== Filter helpers ====================

    [Fact]
    public void FilterSchemaNames_Meta_Sentinel_Is_Stripped()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/",
            FilterSchemaNames = new() { "meta" } };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldNotContain("schema_shortname");
    }

    [Fact]
    public void FilterSchemaNames_NonMeta_Applied()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/",
            FilterSchemaNames = new() { "user_profile" } };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain("schema_shortname");
    }

    [Fact]
    public void ExactSubpath_Uses_Equality_Not_LIKE()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/api",
            ExactSubpath = true };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain("subpath = $");
        where.ShouldNotContain("LIKE");
    }

    [Fact]
    public void DateRange_Filters_Applied()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/",
            FromDate = new DateTime(2025, 1, 1), ToDate = new DateTime(2025, 12, 31) };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain("created_at >=");
        where.ShouldContain("created_at <=");
    }

    [Fact]
    public void Random_OrderBy_Uses_RANDOM()
    {
        var q = new Query { Type = QueryType.Random, SpaceName = "test", Subpath = "/" };
        var args = new List<NpgsqlParameter>();
        var sql = new System.Text.StringBuilder();
        QueryHelper.AppendOrderAndPaging(sql, q, args);
        sql.ToString().ShouldContain("ORDER BY RANDOM()");
    }
}
