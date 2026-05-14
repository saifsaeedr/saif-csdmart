using System.Text;
using Dmart.SqlAdapter.Permissions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.SqlAdapter;

// Pure-string tests for the ACL filter — no DB. Verifies skip-list behaviour,
// no-op cases, the owner / ACL-EXISTS shape, and that LIKE-special chars
// inside query-policy patterns are escaped under `ESCAPE '\'`.
public class PermissionFilterTests
{
    [Theory]
    [InlineData("attachments")]
    [InlineData("histories")]
    public void Append_Skips_Excluded_Tables(string tableName)
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", tableName, new List<string> { "*" });

        sql.ToString().ShouldBe("space_name = @space");  // unchanged
        pars.Count.ShouldBe(0);                          // no params added
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Append_Skips_When_Actor_Missing(string? actor)
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, actor, "entries", new List<string> { "*" });

        sql.ToString().ShouldBe("space_name = @space");
        pars.Count.ShouldBe(0);
    }

    [Fact]
    public void Append_With_No_Policies_Emits_Owner_And_Acl_Clauses()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", "entries", queryPolicies: null);

        var emitted = sql.ToString();
        emitted.ShouldContain("AND (");
        emitted.ShouldContain("owner_shortname = @perm_actor");
        emitted.ShouldContain("jsonb_array_elements");
        emitted.ShouldContain("'allowed_actions') ? 'query'");
        // One @perm_actor only; no policy params.
        pars.Count.ShouldBe(1);
        pars[0].ParameterName.ShouldBe("@perm_actor");
        pars[0].Value.ShouldBe("alice");
    }

    [Fact]
    public void Append_Includes_Policy_Like_Conditions_With_Escape_Clause()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", "entries",
            new List<string> { "myspace:foo:*:*:*", "myspace:bar:*:*:*" });

        var emitted = sql.ToString();
        emitted.ShouldContain("unnest(query_policies)");
        emitted.ShouldContain("@perm_qp0");
        emitted.ShouldContain("@perm_qp1");
        emitted.ShouldContain("ESCAPE '\\'");
        // 1 actor + 2 policies.
        pars.Count.ShouldBe(3);
    }

    [Fact]
    public void Append_Escapes_Like_Metacharacters_In_Policy_Patterns()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        // Pattern that contains every metachar we care about: % and _ must
        // become \% and \_ ; * must expand to %; \ must escape itself first.
        PermissionFilter.Append(sql, pars, "alice", "entries",
            new List<string> { @"my%space:bar_baz:*\thing:*:*" });

        // Find the policy parameter (after @perm_actor).
        var policyParam = pars.Single(p => p.ParameterName == "@perm_qp0");
        var pattern = (string)policyParam.Value!;

        // Order: \ escaped first, then % and _, then * → %.
        pattern.ShouldBe(@"my\%space:bar\_baz:%\\thing:%:%");
    }

    [Fact]
    public void Append_With_Empty_Policy_List_Still_Emits_Owner_And_Acl_Clauses()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", "entries", new List<string>());

        var emitted = sql.ToString();
        emitted.ShouldContain("owner_shortname = @perm_actor");
        emitted.ShouldNotContain("unnest(query_policies)");
        pars.Count.ShouldBe(1);
    }
}
