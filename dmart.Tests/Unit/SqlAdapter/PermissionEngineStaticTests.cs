using Dmart.SqlAdapter.Permissions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.SqlAdapter;

// Pure static-helper tests for PermissionEngine — these guard the subpath-walk
// and pattern-rewrite logic that drive every permission check, without
// touching the database. Visibility on the helpers is `internal` and exposed
// via InternalsVisibleTo("dmart.Tests") in Dmart.SqlAdapter.csproj.
public class PermissionEngineStaticTests
{
    [Fact]
    public void BuildSubpathWalk_Root_Returns_Just_Slash()
    {
        var walk = PermissionEngine.BuildSubpathWalk("/");
        walk.ShouldBe(new List<string> { "/" });
    }

    [Fact]
    public void BuildSubpathWalk_Empty_Returns_Just_Slash()
    {
        var walk = PermissionEngine.BuildSubpathWalk("");
        walk.ShouldBe(new List<string> { "/" });
    }

    [Fact]
    public void BuildSubpathWalk_Nested_Path_Builds_Cumulative_Segments()
    {
        // dmart's permission walk stores subpath segments without the leading
        // slash (NormalizePermissionSubpath strips it).
        var walk = PermissionEngine.BuildSubpathWalk("/a/b/c");
        walk.ShouldBe(new List<string> { "/", "a", "a/b", "a/b/c" });
    }

    [Fact]
    public void BuildSubpathWalk_Trims_Slashes_From_Both_Ends()
    {
        var walk = PermissionEngine.BuildSubpathWalk("//foo//bar//");
        // Empty segments (from the doubled slash) are dropped by SplitOptions.
        walk.ShouldBe(new List<string> { "/", "foo", "foo/bar" });
    }

    [Fact]
    public void ToGlobalForm_Root_Maps_To_AllSubpaths()
    {
        PermissionEngine.ToGlobalForm("/").ShouldBe(PermissionEngine.AllSubpathsMw);
        PermissionEngine.ToGlobalForm("").ShouldBe(PermissionEngine.AllSubpathsMw);
    }

    [Fact]
    public void ToGlobalForm_Single_Segment_Maps_To_AllSubpaths()
    {
        // Single-segment paths have no parent to globalize, so the function
        // returns the wildcard sentinel directly.
        PermissionEngine.ToGlobalForm("foo").ShouldBe(PermissionEngine.AllSubpathsMw);
    }

    [Fact]
    public void ToGlobalForm_Replaces_Penultimate_Segment_With_AllSubpaths()
    {
        // For "a/b/c" the penultimate segment is "b" — globalizing it yields
        // "a/__all_subpaths__/c", letting permissions on "a/*/c" match this row.
        PermissionEngine.ToGlobalForm("a/b/c")
            .ShouldBe($"a/{PermissionEngine.AllSubpathsMw}/c");
    }

    [Fact]
    public void Constants_Carry_Expected_Sentinel_Strings()
    {
        // The walk + match logic relies on these literal sentinels matching
        // what the dmart server writes into permission rows.
        PermissionEngine.AllSpacesMw.ShouldBe("__all_spaces__");
        PermissionEngine.AllSubpathsMw.ShouldBe("__all_subpaths__");
        PermissionEngine.CurrentUserMw.ShouldBe("__current_user__");
        PermissionEngine.CurrentUserOwnerMw.ShouldBe("__current_user__owner__");
        PermissionEngine.AnonymousUser.ShouldBe("anonymous");
    }
}
