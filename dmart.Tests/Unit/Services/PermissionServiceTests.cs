using System.Collections.Generic;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for the pure helpers in PermissionService — no DB, no repos.
// Verifies dmart-Python parity for the hierarchical subpath walk, the
// __all_subpaths__ "global form" rewrite, condition predicates, and field
// restriction checks.
public class PermissionServiceTests
{
    // ==================== BuildSubpathWalk ====================

    [Fact]
    public void Walk_Root_Returns_Just_Slash()
    {
        var w = PermissionService.BuildSubpathWalk("/");
        w.ShouldBe(new[] { "/" });
    }

    [Fact]
    public void Walk_Empty_Returns_Just_Slash()
    {
        PermissionService.BuildSubpathWalk("").ShouldBe(new[] { "/" });
    }

    [Fact]
    public void Walk_Single_Segment_Adds_One_Step()
    {
        PermissionService.BuildSubpathWalk("/foo").ShouldBe(new[] { "/", "foo" });
    }

    [Fact]
    public void Walk_Three_Segments_Iterates_Each_Level()
    {
        PermissionService.BuildSubpathWalk("/a/b/c")
            .ShouldBe(new[] { "/", "a", "a/b", "a/b/c" });
    }

    [Fact]
    public void Walk_Strips_Leading_And_Trailing_Slashes()
    {
        PermissionService.BuildSubpathWalk("/people/alice/messages/")
            .ShouldBe(new[] { "/", "people", "people/alice", "people/alice/messages" });
    }

    // ==================== ToGlobalForm (dmart's __all_subpaths__ rewrite) ====================

    [Fact]
    public void GlobalForm_Of_Root_Is_Magic_Word()
    {
        PermissionService.ToGlobalForm("/").ShouldBe("__all_subpaths__");
    }

    [Fact]
    public void GlobalForm_Of_Single_Segment_Is_Magic_Word()
    {
        PermissionService.ToGlobalForm("foo").ShouldBe("__all_subpaths__");
    }

    [Fact]
    public void GlobalForm_Of_Two_Segments_Replaces_First()
    {
        // parts = ["a","b"]; parts[-2] = "__all_subpaths__" → "__all_subpaths__/b"
        PermissionService.ToGlobalForm("a/b").ShouldBe("__all_subpaths__/b");
    }

    [Fact]
    public void GlobalForm_Of_Three_Segments_Replaces_Middle()
    {
        // parts = ["people","alice","messages"]; parts[-2] = "__all_subpaths__"
        //   → "people/__all_subpaths__/messages"
        PermissionService.ToGlobalForm("people/alice/messages")
            .ShouldBe("people/__all_subpaths__/messages");
    }

    // ==================== CheckConditions ====================

    [Fact]
    public void Conditions_Are_Skipped_For_Create()
    {
        PermissionService.CheckConditions(
            new() { "is_active", "own" },
            new(),  // achieved nothing
            "create").ShouldBeTrue();
    }

    [Fact]
    public void Conditions_Are_Skipped_For_Query()
    {
        PermissionService.CheckConditions(
            new() { "own" }, new(), "query").ShouldBeTrue();
    }

    [Fact]
    public void Conditions_Pass_When_All_Required_Achieved()
    {
        PermissionService.CheckConditions(
            new() { "own" },
            new() { "own", "is_active" },
            "update").ShouldBeTrue();
    }

    [Fact]
    public void Conditions_Fail_When_Required_Not_Achieved()
    {
        PermissionService.CheckConditions(
            new() { "own" },
            new() { "is_active" },
            "update").ShouldBeFalse();
    }

    [Fact]
    public void Conditions_Pass_When_None_Required()
    {
        PermissionService.CheckConditions(
            new(), new(), "update").ShouldBeTrue();
    }

    // ==================== CheckRestrictions: restricted_fields ====================

    [Fact]
    public void Restrictions_Skipped_For_View()
    {
        PermissionService.CheckRestrictions(
            new() { "secret" }, null, "view",
            new() { ["secret"] = "anything" }).ShouldBeTrue();
    }

    [Fact]
    public void Restrictions_Block_Direct_Field_On_Create()
    {
        PermissionService.CheckRestrictions(
            new() { "secret" }, null, "create",
            new() { ["secret"] = "leaked" }).ShouldBeFalse();
    }

    [Fact]
    public void Restrictions_Block_Nested_Field_Via_Dot_Prefix()
    {
        PermissionService.CheckRestrictions(
            new() { "payload" }, null, "update",
            new()
            {
                ["payload"] = new Dictionary<string, object> { ["secret"] = "x" },
            }).ShouldBeFalse();
    }

    [Fact]
    public void Restrictions_Allow_When_No_Match()
    {
        PermissionService.CheckRestrictions(
            new() { "secret" }, null, "create",
            new() { ["public"] = "ok" }).ShouldBeTrue();
    }

    // ==================== CheckRestrictions: allowed_fields_values ====================

    [Fact]
    public void AllowedValues_Pass_When_Scalar_Matches()
    {
        PermissionService.CheckRestrictions(
            null,
            new() { ["status"] = new List<object> { "draft", "published" } },
            "update",
            new() { ["status"] = "draft" }).ShouldBeTrue();
    }

    [Fact]
    public void AllowedValues_Fail_When_Scalar_Not_In_Set()
    {
        PermissionService.CheckRestrictions(
            null,
            new() { ["status"] = new List<object> { "draft", "published" } },
            "update",
            new() { ["status"] = "deleted" }).ShouldBeFalse();
    }

    [Fact]
    public void AllowedValues_Skipped_When_Field_Absent_From_Patch()
    {
        // If the user isn't trying to set this field, no restriction applies.
        PermissionService.CheckRestrictions(
            null,
            new() { ["status"] = new List<object> { "draft" } },
            "update",
            new() { ["other"] = "x" }).ShouldBeTrue();
    }

    // ==================== MatchesAnyPattern (__current_user__ + __all_subpaths__) ====================

    [Fact]
    public void Pattern_Matches_Literal_Subpath()
    {
        PermissionService.MatchesAnyPattern(
            new() { "people/alice/protected" },
            "people/alice/protected",
            "alice").ShouldBeTrue();
    }

    [Fact]
    public void Pattern_AllSubpaths_Matches_Anything()
    {
        PermissionService.MatchesAnyPattern(
            new() { "__all_subpaths__" },
            "people/alice/anything",
            "alice").ShouldBeTrue();
    }

    [Fact]
    public void Pattern_CurrentUser_Resolves_To_Actor_Shortname()
    {
        PermissionService.MatchesAnyPattern(
            new() { "people/__current_user__/protected" },
            "people/alice/protected",
            "alice").ShouldBeTrue();
    }

    [Fact]
    public void Pattern_CurrentUser_Does_Not_Match_Other_Users_Paths()
    {
        // Actor "alice" should NOT match a pattern resolving to "people/alice/..."
        // against a candidate like "people/bob/...".
        PermissionService.MatchesAnyPattern(
            new() { "people/__current_user__/protected" },
            "people/bob/protected",
            "alice").ShouldBeFalse();
    }

    [Fact]
    public void Pattern_CurrentUser_Requires_Actor()
    {
        PermissionService.MatchesAnyPattern(
            new() { "people/__current_user__/protected" },
            "people/alice/protected",
            actor: null).ShouldBeFalse();
    }

    [Fact]
    public void Pattern_Matches_Global_Form_For_AllSubpaths_Wildcard()
    {
        // An access_protected-style pattern: people/__all_subpaths__/protected.
        // The caller passes the ToGlobalForm-rewritten candidate for a walk step
        // like "people/bob/protected", which becomes "people/__all_subpaths__/protected".
        PermissionService.MatchesAnyPattern(
            new() { "people/__all_subpaths__/protected" },
            "people/__all_subpaths__/protected",
            "anyone").ShouldBeTrue();
    }

    // ==================== Slash normalization (Python trans_magic_words parity) ====================

    [Fact]
    public void Pattern_LeadingSlash_Matches_Slashless_Walk_Key()
    {
        // Real-world shape: `permissions.world` stored with `"/denominations"`
        // vs the walk's `"denominations"` (slash-stripped by BuildSubpathWalk).
        // Without the normalization these never matched → anonymous got 0 results.
        PermissionService.MatchesAnyPattern(
            new() { "/denominations" },
            "denominations",
            actor: null).ShouldBeTrue();
    }

    [Fact]
    public void Pattern_TrailingSlash_Matches_Slashless_Walk_Key()
    {
        PermissionService.MatchesAnyPattern(
            new() { "denominations/" },
            "denominations",
            actor: null).ShouldBeTrue();
    }

    [Fact]
    public void Pattern_DoubleSlash_Collapses()
    {
        PermissionService.MatchesAnyPattern(
            new() { "a//b" },
            "a/b",
            actor: null).ShouldBeTrue();
    }

    [Fact]
    public void Pattern_RootSlash_StaysRoot()
    {
        PermissionService.MatchesAnyPattern(
            new() { "/" },
            "/",
            actor: null).ShouldBeTrue();
    }

    [Fact]
    public void NormalizePermissionSubpath_Pure_Cases()
    {
        PermissionService.NormalizePermissionSubpath("/denominations").ShouldBe("denominations");
        PermissionService.NormalizePermissionSubpath("denominations/").ShouldBe("denominations");
        PermissionService.NormalizePermissionSubpath("/a/b/").ShouldBe("a/b");
        PermissionService.NormalizePermissionSubpath("a//b").ShouldBe("a/b");
        PermissionService.NormalizePermissionSubpath("/").ShouldBe("/");
        PermissionService.NormalizePermissionSubpath("").ShouldBe("/");
        PermissionService.NormalizePermissionSubpath("denominations").ShouldBe("denominations");
    }

    // ==================== FlattenAttrs ====================

    [Fact]
    public void Flatten_Nested_Dict_Uses_Dot_Separator()
    {
        var dest = new Dictionary<string, object?>();
        var src = new Dictionary<string, object>
        {
            ["a"] = "1",
            ["b"] = new Dictionary<string, object>
            {
                ["c"] = "2",
                ["d"] = new Dictionary<string, object> { ["e"] = "3" },
            },
        };
        PermissionService.FlattenAttrs(src, "", dest);

        dest["a"].ShouldBe("1");
        dest["b.c"].ShouldBe("2");
        dest["b.d.e"].ShouldBe("3");
    }
}
