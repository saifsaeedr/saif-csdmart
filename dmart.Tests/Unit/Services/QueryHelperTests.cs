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
    private static string BuildSearch(string search)
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/", Search = search };
        var args = new List<NpgsqlParameter>();
        return QueryHelper.BuildWhereClause(q, args);
    }

    // ==================== Plain text search ====================

    [Fact]
    public void Search_PlainText_Generates_ILIKE_On_Common_Fields()
    {
        var where = BuildSearch("hello");
        where.ShouldContain("ILIKE");
        where.ShouldContain("payload::text");
        where.ShouldContain("displayname::text");
    }

    // ==================== @field:value basic ====================

    [Fact]
    public void Search_FieldValue_Generates_Jsonb_Path()
    {
        var where = BuildSearch("@payload.body.email:test@example.com");
        where.ShouldContain("payload::jsonb");
        where.ShouldContain("'body'");
        where.ShouldContain("'email'");
    }

    // ==================== Negation ====================

    [Fact]
    public void Search_Negation_Generates_NotEqual()
    {
        var where = BuildSearch("-@status:deleted");
        where.ShouldContain("!=");
        where.ShouldContain("status");
    }

    [Fact]
    public void Search_Negation_Payload_Generates_Not()
    {
        var where = BuildSearch("-@payload.body.k:v");
        where.ShouldContain("NOT");
        where.ShouldContain("payload::jsonb");
    }

    // ==================== Comparison operators ====================

    [Fact]
    public void Search_Comparison_Greater_Generates_Numeric_Compare()
    {
        var where = BuildSearch("@payload.body.price:>100");
        where.ShouldContain(">");
        where.ShouldContain("::float");
    }

    [Fact]
    public void Search_Comparison_LessEqual()
    {
        var where = BuildSearch("@payload.body.count:<=50");
        where.ShouldContain("<=");
    }

    // ==================== OR pipe ====================

    [Fact]
    public void Search_OrPipe_Generates_OR_Clauses()
    {
        var where = BuildSearch("@status:active|pending");
        where.ShouldContain(" OR ");
    }

    [Fact]
    public void Search_Negation_OrPipe_Generates_AND()
    {
        var where = BuildSearch("-@payload.body.k:v1|v2");
        // Negation with OR values → AND (DeMorgan's)
        where.ShouldContain(" AND ");
    }

    // ==================== Multiple fields ====================

    [Fact]
    public void Search_MultipleFields_Generates_AND_Clauses()
    {
        var where = BuildSearch("@payload.body.k:v @payload.body.a:b");
        where.ShouldContain(" AND ");
    }

    // ==================== JSONB array columns ====================

    [Fact]
    public void Search_Roles_Uses_JsonbContainment()
    {
        var where = BuildSearch("@roles:admin");
        where.ShouldContain("@>");
        where.ShouldContain("jsonb_typeof(roles)");
    }

    [Fact]
    public void Search_Tags_Uses_JsonbContainment()
    {
        var where = BuildSearch("@tags:important");
        where.ShouldContain("@>");
        where.ShouldContain("jsonb_typeof(tags)");
    }

    [Fact]
    public void Search_Negation_Roles_Uses_NOT()
    {
        var where = BuildSearch("-@roles:admin");
        where.ShouldContain("NOT");
        where.ShouldContain("@>");
    }

    // ==================== Wildcard paths ====================

    [Fact]
    public void Search_Payload_Body_Wildcard_Searches_Text()
    {
        var where = BuildSearch("@payload.body.*:hello");
        where.ShouldContain("payload::jsonb->'body'");
        where.ShouldContain("::text ILIKE");
    }

    [Fact]
    public void Search_Payload_Wildcard_Searches_Entire_Payload()
    {
        var where = BuildSearch("@payload.*:something");
        where.ShouldContain("payload::jsonb");
        where.ShouldContain("::text ILIKE");
    }

    // ==================== Range queries ====================

    [Fact]
    public void Search_Range_Numeric_Generates_BETWEEN()
    {
        var where = BuildSearch("@payload.body.price:[10 100]");
        where.ShouldContain("BETWEEN");
        where.ShouldContain("::float");
    }

    [Fact]
    public void Search_Range_Comma_Separated()
    {
        var where = BuildSearch("@payload.body.age:[18,65]");
        where.ShouldContain("BETWEEN");
    }

    [Fact]
    public void Search_Range_String_Generates_BETWEEN()
    {
        var where = BuildSearch("@payload.body.date:[2024-01-01,2024-12-31]");
        where.ShouldContain("BETWEEN");
    }

    // ==================== And keyword + same field ====================

    [Fact]
    public void Search_And_Keyword_Is_Ignored()
    {
        var where = BuildSearch("@payload.body.k:v1 and @payload.body.k:v2");
        // "and" is skipped; same field accumulates → k has [v1, v2] with AND
        where.ShouldContain("AND");
        where.ShouldContain("payload::jsonb");
    }

    // ==================== Parentheses grouping ====================

    [Fact]
    public void Search_Parentheses_Creates_OR_Between_Groups()
    {
        var where = BuildSearch("(@is_active:true) (@payload.body.k:v)");
        where.ShouldContain(" OR ");
    }

    [Fact]
    public void Search_Parentheses_AND_Within_Group()
    {
        var where = BuildSearch("(@payload.body.a:1 @payload.body.b:2)");
        where.ShouldContain(" AND ");
    }

    // ==================== Boolean columns ====================

    [Fact]
    public void Search_Boolean_Column_Uses_Cast()
    {
        var where = BuildSearch("@is_active:true");
        where.ShouldContain("CAST(is_active AS BOOLEAN)");
    }

    // ==================== Type-aware payload ====================

    [Fact]
    public void Search_Payload_Boolean_Value()
    {
        var where = BuildSearch("@payload.body.enabled:true");
        where.ShouldContain("::boolean");
        where.ShouldContain("jsonb_typeof");
    }

    [Fact]
    public void Search_Payload_Exact_String_Match()
    {
        var where = BuildSearch("@payload.body.name:john");
        where.ShouldContain("jsonb_typeof");
        where.ShouldContain("'string'");
    }

    [Fact]
    public void Search_Payload_Array_Containment()
    {
        var where = BuildSearch("@payload.body.tags:alpha");
        where.ShouldContain("@>");
        where.ShouldContain("'array'");
    }

    [Fact]
    public void Search_Payload_Path_Match_Includes_Root_Containment_For_Gin_Index()
    {
        // For a value lookup like @payload.body.brand_shortname:abc, we MUST
        // emit a `payload @> '{"body":{"brand_shortname":"..."}}'::jsonb`
        // branch alongside the existing typeof/string/jsonb predicates.
        // Without it, the OR chain has no index-eligible expression and the
        // planner falls through to a seq scan — fatal under client-side
        // joins that explode into 100-OR'd values against /products etc.
        // (see QueryService.ApplyClientJoinsAsync widening).
        var where = BuildSearch("@payload.body.brand_shortname:abc");
        where.ShouldContain("payload::jsonb @>");
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

    // ==================== Combined scenarios from spec ====================

    // @roles:x  — x in roles (str[])
    [Fact]
    public void Spec_Roles_Array_Search()
    {
        var where = BuildSearch("@roles:super_admin");
        where.ShouldContain("@>");
        where.ShouldContain("jsonb_typeof(roles)");
        where.ShouldContain("CAST($"); // JSON param is in parameter
    }

    // @payload.body.k:v  — k and v can be any type
    [Fact]
    public void Spec_Payload_Body_KeyValue()
    {
        var where = BuildSearch("@payload.body.hostname:web01");
        where.ShouldContain("payload::jsonb->'body'->>'hostname'");
        where.ShouldContain("jsonb_typeof");
    }

    // @payload.body.*:v  — wildcard searches all body keys
    [Fact]
    public void Spec_Payload_Body_Wildcard()
    {
        var where = BuildSearch("@payload.body.*:web01");
        where.ShouldContain("(payload::jsonb->'body')::text ILIKE");
    }

    // @payload.*:v  — wildcard searches all payload keys
    [Fact]
    public void Spec_Payload_Wildcard()
    {
        var where = BuildSearch("@payload.*:json");
        where.ShouldContain("(payload::jsonb)::text ILIKE");
    }

    // v  — search everywhere in all cols for string v
    [Fact]
    public void Spec_PlainText_Everywhere()
    {
        var where = BuildSearch("web01");
        where.ShouldContain("shortname ILIKE");
        where.ShouldContain("payload::text ILIKE");
        where.ShouldContain("displayname::text ILIKE");
        where.ShouldContain("description::text ILIKE");
        where.ShouldContain("tags::text ILIKE");
    }

    // @payload.body.k:v1|v2  — or operator
    [Fact]
    public void Spec_Or_Operator()
    {
        var where = BuildSearch("@payload.body.status:active|pending");
        where.ShouldContain(" OR ");
        // Two value conditions joined by OR
        var orCount = System.Text.RegularExpressions.Regex.Matches(where, " OR ").Count;
        orCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    // -@payload.body.k:v  — k not v
    [Fact]
    public void Spec_Negation_PayloadBody()
    {
        var where = BuildSearch("-@payload.body.status:deleted");
        where.ShouldContain("!=");
        where.ShouldContain("NOT");
    }

    // -@payload.body.k:v1|v2  — k not v1 or v2
    [Fact]
    public void Spec_Negation_Or()
    {
        var where = BuildSearch("-@payload.body.env:staging|dev");
        where.ShouldContain("!=");
        // Negation + OR → AND between negated conditions
        where.ShouldContain(" AND ");
    }

    // @payload.body.k:v @payload.body.a:b  — k==v and a==b
    [Fact]
    public void Spec_Two_Fields_And()
    {
        var where = BuildSearch("@payload.body.host:web01 @payload.body.dc:us-east");
        // Two separate field conditions joined by AND
        where.ShouldContain("'host'");
        where.ShouldContain("'dc'");
        where.ShouldContain(" AND ");
    }

    // @payload.body.k:v1|v2 @payload.body.a:b  — k==(v1 or v2) and a==b
    [Fact]
    public void Spec_OrField_And_ExactField()
    {
        var where = BuildSearch("@payload.body.env:staging|prod @payload.body.region:us");
        where.ShouldContain("'env'");
        where.ShouldContain("'region'");
        where.ShouldContain(" OR ");
        where.ShouldContain(" AND ");
    }

    // @payload.body.k:v1 and @payload.body.k:v2  — k==v1 and k==v2 (array)
    [Fact]
    public void Spec_SameField_And_Accumulation()
    {
        var where = BuildSearch("@payload.body.tags:alpha and @payload.body.tags:beta");
        // Same field "tags" should accumulate both values
        where.ShouldContain("'tags'");
        where.ShouldContain(" AND ");
    }

    // @payload.body.k:[v1 v2]  — between (numeric)
    [Fact]
    public void Spec_Range_Numeric()
    {
        var where = BuildSearch("@payload.body.price:[10 100]");
        where.ShouldContain("BETWEEN");
        where.ShouldContain("::float");
        where.ShouldContain("'number'");
    }

    // @payload.body.k:[v1,v2]  — between (date-like strings)
    [Fact]
    public void Spec_Range_Date_Strings()
    {
        var where = BuildSearch("@payload.body.created:[2024-01-01,2024-12-31]");
        where.ShouldContain("BETWEEN");
    }

    // (A B) and C  — parentheses grouping
    [Fact]
    public void Spec_Parens_GroupingWithAnd()
    {
        var where = BuildSearch("(@is_active:true @roles:admin) and @payload.body.k:v");
        // Two groups joined by OR (Python semantics: AND within, OR between)
        where.ShouldContain(" OR ");
        // Group 1 has is_active AND roles
        where.ShouldContain("CAST(is_active AS BOOLEAN)");
        where.ShouldContain("jsonb_typeof(roles)");
        // Group 2 has payload condition
        where.ShouldContain("payload::jsonb");
    }

    // @payload.body.k:>v  — greater than
    [Fact]
    public void Spec_GreaterThan()
    {
        var where = BuildSearch("@payload.body.cpu:>80");
        where.ShouldContain(">");
        where.ShouldContain("::float");
    }

    // @payload.body.k:>=v
    [Fact]
    public void Spec_GreaterEqual()
    {
        var where = BuildSearch("@payload.body.memory:>=16");
        where.ShouldContain(">=");
    }

    // @payload.body.k:<v
    [Fact]
    public void Spec_LessThan()
    {
        var where = BuildSearch("@payload.body.latency:<100");
        where.ShouldContain("<");
    }

    // @payload.body.k:<=v
    [Fact]
    public void Spec_LessEqual()
    {
        var where = BuildSearch("@payload.body.errors:<=5");
        where.ShouldContain("<=");
    }

    // ==================== Edge cases ====================

    [Fact]
    public void Search_Quoted_Value_With_Spaces()
    {
        var where = BuildSearch("@payload.body.name:\"John Doe\"");
        where.ShouldContain("payload::jsonb");
    }

    [Fact]
    public void Search_NestedPath_Three_Levels()
    {
        var where = BuildSearch("@payload.body.config.db.host:localhost");
        where.ShouldContain("'config'");
        where.ShouldContain("'db'");
        where.ShouldContain("'host'");
    }

    [Fact]
    public void Search_Empty_String_Does_Not_Crash()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "test", Subpath = "/", Search = "" };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Search_Multiple_Fields_Are_Single_Group()
    {
        var where = BuildSearch("@payload.body.a:x @payload.body.b:y @payload.body.c:z");
        // All in same group → top-level conditions joined by AND.
        // The type-aware payload SQL has OR inside each value match but
        // the field-level conditions are joined by AND.
        where.ShouldContain("'a'");
        where.ShouldContain("'b'");
        where.ShouldContain("'c'");
        where.ShouldContain(" AND ");
    }

    [Fact]
    public void Search_Numeric_Value_Gets_Type_Detection()
    {
        var where = BuildSearch("@payload.body.count:42");
        // Numeric value → should check for number type in jsonb
        where.ShouldContain("'number'");
        where.ShouldContain("::float");
    }

    [Fact]
    public void Search_Boolean_Value_Gets_Type_Detection()
    {
        var where = BuildSearch("@payload.body.active:false");
        where.ShouldContain("::boolean");
        where.ShouldContain("'boolean'");
    }

    [Fact]
    public void Search_Negation_Range()
    {
        var where = BuildSearch("-@payload.body.score:[0 50]");
        where.ShouldContain("NOT BETWEEN");
    }

    [Fact]
    public void Search_Mixed_PlainText_And_Field()
    {
        var where = BuildSearch("web01 @payload.body.dc:us-east");
        // Plain text search AND field search
        where.ShouldContain("shortname ILIKE");
        where.ShouldContain("'dc'");
        where.ShouldContain(" AND ");
    }

    [Fact]
    public void Search_Direct_Column_Exact_Match()
    {
        // Non-boolean, non-array column with a plain value → exact `=`,
        // so the column's btree (e.g. the entries unique key on shortname,
        // or idx_entries_slug) actually fires.
        var where = BuildSearch("@shortname:myentry");
        where.ShouldContain("shortname::text =");
        where.ShouldNotContain("ILIKE");
    }

    [Fact]
    public void Search_Direct_Column_Wildcard_Falls_Back_To_ILIKE()
    {
        // Explicit `*` in the value → glob matching. `*` is translated to
        // `%` so the user can do prefix/suffix searches the index can
        // still partially serve (`abc%` is sargable on a btree).
        var where = BuildSearch("@shortname:web*");
        where.ShouldContain("shortname::text ILIKE");
    }

    [Fact]
    public void Search_IsOpen_Boolean_Column()
    {
        var where = BuildSearch("@is_open:false");
        where.ShouldContain("CAST(is_open AS BOOLEAN)");
    }

    // ==================== Existence check @k:* ====================

    [Fact]
    public void Search_Existence_Column_IsNotNull()
    {
        var where = BuildSearch("@slug:*");
        where.ShouldContain("slug IS NOT NULL");
    }

    [Fact]
    public void Search_Existence_Column_Negated_IsNull()
    {
        var where = BuildSearch("-@slug:*");
        where.ShouldContain("slug IS NULL");
    }

    [Fact]
    public void Search_Existence_Payload_Field_IsNotNull()
    {
        var where = BuildSearch("@payload.body.email:*");
        where.ShouldContain("payload::jsonb->'body'->'email' IS NOT NULL");
    }

    [Fact]
    public void Search_Existence_Payload_Field_Negated_IsNull()
    {
        var where = BuildSearch("-@payload.body.email:*");
        where.ShouldContain("payload::jsonb->'body'->'email' IS NULL");
    }

    [Fact]
    public void Search_Existence_Nested_Payload_Path()
    {
        var where = BuildSearch("@payload.body.config.db:*");
        where.ShouldContain("payload::jsonb->'body'->'config'->'db' IS NOT NULL");
    }

    // ==================== sort_by / sort_type ====================

    private static string BuildOrder(Query q, string? tableName = null)
    {
        var sql = new System.Text.StringBuilder();
        var args = new List<NpgsqlParameter>();
        QueryHelper.AppendOrderAndPaging(sql, q, args, tableName);
        return sql.ToString();
    }

    [Fact]
    public void SortBy_Null_OrdersByUpdatedAt_Desc()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "t", Subpath = "/" };
        BuildOrder(q, "entries").ShouldContain("ORDER BY updated_at DESC");
    }

    [Fact]
    public void SortBy_Shortname_Asc_EmitsOrderByShortnameAsc()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "t", Subpath = "/",
            SortBy = "shortname", SortType = SortType.Ascending };
        BuildOrder(q, "entries").ShouldContain("ORDER BY shortname ASC");
    }

    [Fact]
    public void SortBy_CreatedAt_Desc_EmitsOrderByCreatedAtDesc()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "t", Subpath = "/",
            SortBy = "created_at", SortType = SortType.Descending };
        BuildOrder(q, "entries").ShouldContain("ORDER BY created_at DESC");
    }

    [Fact]
    public void SortBy_AttributesPrefix_Stripped()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "t", Subpath = "/",
            SortBy = "attributes.shortname" };
        BuildOrder(q, "entries").ShouldContain("ORDER BY shortname");
    }

    [Fact]
    public void SortBy_Unknown_FallsBackToUpdatedAt()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "t", Subpath = "/",
            SortBy = "; DROP TABLE entries; --" };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("ORDER BY updated_at");
        order.ShouldNotContain("DROP");
    }

    [Fact]
    public void SortBy_UserTableSpecific_Column_Allowed()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "t", Subpath = "/",
            SortBy = "email", SortType = SortType.Ascending };
        BuildOrder(q, "users").ShouldContain("ORDER BY email ASC");
    }

    [Fact]
    public void SortBy_EntryTableColumn_Rejected_On_UserTable()
    {
        // state is whitelisted for entries but not users — caller on users table must fall back.
        var q = new Query { Type = QueryType.Subpath, SpaceName = "t", Subpath = "/",
            SortBy = "state" };
        BuildOrder(q, "users").ShouldContain("ORDER BY updated_at");
    }

    [Fact]
    public void SortBy_RandomQuery_Ignores_SortBy()
    {
        var q = new Query { Type = QueryType.Random, SpaceName = "t", Subpath = "/",
            SortBy = "shortname" };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("ORDER BY RANDOM()");
        order.ShouldNotContain("shortname");
    }

    // ---- JSON-path sort (Python-parity transform_keys_to_sql) ----

    [Fact]
    public void SortBy_JsonPath_TwoLevel_EmitsArrowArrowGtGt()
    {
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "payload.body.rank", SortType = SortType.Ascending };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("payload::jsonb -> 'body' ->> 'rank'");
        // Numeric-aware CASE wrap
        order.ShouldContain("CASE WHEN");
        order.ShouldContain("::float");
        // Direction applied to both CASE and fallback text sort
        order.ShouldNotContain(" DESC");
    }

    [Fact]
    public void SortBy_JsonPath_SingleLevel_UsesArrowGtGt()
    {
        // payload.rank → payload::jsonb ->> 'rank' (no middle -> hops)
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "payload.rank", SortType = SortType.Descending };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("payload::jsonb ->> 'rank'");
        order.ShouldContain(" DESC");
    }

    [Fact]
    public void SortBy_BodyShortcut_PrefixesWithPayload()
    {
        // Python shortcut: sort_by="body.rank" → payload.body.rank
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "body.rank" };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("payload::jsonb -> 'body' ->> 'rank'");
    }

    [Fact]
    public void SortBy_AtPrefix_StrippedOnJsonPath()
    {
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "@payload.body.rank" };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("payload::jsonb -> 'body' ->> 'rank'");
        order.ShouldNotContain("@payload");
    }

    [Fact]
    public void SortBy_JsonPath_UnsafeSegment_Skipped()
    {
        // Segments must be alphanumeric/underscore. "rank; DROP TABLE" is not.
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "payload.body.rank; DROP TABLE entries" };
        var order = BuildOrder(q, "entries");
        // Nothing resolves → fallback
        order.ShouldContain("ORDER BY updated_at");
        order.ShouldNotContain("DROP");
    }

    // ---- Comma-separated multi-sort ----

    [Fact]
    public void SortBy_CommaList_BothPathAndColumn_EmitsBoth()
    {
        // The user's actual failing case: "payload.body.rank, shortname"
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "payload.body.rank, shortname", SortType = SortType.Ascending };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("payload::jsonb -> 'body' ->> 'rank'");
        order.ShouldContain("shortname ASC");
        // A comma separates the two clauses.
        order.ShouldContain(", shortname ASC");
    }

    [Fact]
    public void SortBy_CommaList_AllColumnsWhitelisted()
    {
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "created_at,shortname", SortType = SortType.Descending };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("ORDER BY created_at DESC, shortname DESC");
    }

    [Fact]
    public void SortBy_CommaList_DropsUnknownColumnsKeepsResolvable()
    {
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "not_a_column, shortname" };
        var order = BuildOrder(q, "entries");
        order.ShouldContain("ORDER BY shortname");
        order.ShouldNotContain("not_a_column");
    }

    [Fact]
    public void SortBy_CommaList_AllUnknown_FallsBackToUpdatedAt()
    {
        var q = new Query { Type = QueryType.Search, SpaceName = "t", Subpath = "/",
            SortBy = "bogus1, bogus2" };
        BuildOrder(q, "entries").ShouldContain("ORDER BY updated_at");
    }
}
