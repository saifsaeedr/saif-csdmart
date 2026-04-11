using System.Collections.Generic;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for the pure filter-matching logic in PluginManager. Covers the
// dmart-python-parity behaviors that are easy to regress if someone tweaks the
// predicate: __ALL__ wildcards, hierarchical subpath matching, the
// content+schema_shortname special case, and resource-type gating.
public class PluginManagerTests
{
    private static EventFilter AllFilter(params string[] actions) => new()
    {
        Subpaths = new() { "__ALL__" },
        ResourceTypes = new() { "__ALL__" },
        SchemaShortnames = new() { "__ALL__" },
        Actions = new(actions),
    };

    private static Event Evt(string subpath, ResourceType? type = null, string? schema = null) => new()
    {
        SpaceName = "myspace",
        Subpath = subpath,
        Shortname = "foo",
        ActionType = ActionType.Create,
        ResourceType = type,
        SchemaShortname = schema,
        UserShortname = "tester",
    };

    // ==================== Subpath matching ====================

    [Fact]
    public void AllSubpathsWildcard_Matches_Everything()
    {
        PluginManager.MatchedFilters(AllFilter("create"), Evt("/any/thing"))
            .ShouldBeTrue();
    }

    [Fact]
    public void Literal_Subpath_Matches_Exact()
    {
        var f = AllFilter("create") with { Subpaths = new() { "users" } };
        PluginManager.MatchedFilters(f, Evt("users")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("/users")).ShouldBeTrue();
    }

    [Fact]
    public void Literal_Subpath_Matches_Children()
    {
        // Hierarchical startswith — filter "users" matches event "users/alice"
        // and "users/alice/profile", but NOT "usersearch".
        var f = AllFilter("create") with { Subpaths = new() { "users" } };
        PluginManager.MatchedFilters(f, Evt("users/alice")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("users/alice/profile")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("usersearch")).ShouldBeFalse();
    }

    [Fact]
    public void Literal_Subpath_With_Trailing_Slash_Behaves_Same_As_Stripped()
    {
        var f = AllFilter("create") with { Subpaths = new() { "users/" } };
        PluginManager.MatchedFilters(f, Evt("users")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("users/alice")).ShouldBeTrue();
    }

    [Fact]
    public void Subpath_Mismatch_Rejects()
    {
        var f = AllFilter("create") with { Subpaths = new() { "groups" } };
        PluginManager.MatchedFilters(f, Evt("users")).ShouldBeFalse();
    }

    // ==================== Resource type matching ====================

    [Fact]
    public void ResourceType_All_Wildcard_Matches()
    {
        var f = AllFilter("create");
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Ticket)).ShouldBeTrue();
    }

    [Fact]
    public void ResourceType_Specific_Match()
    {
        var f = AllFilter("create") with { ResourceTypes = new() { "user" } };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.User)).ShouldBeTrue();
    }

    [Fact]
    public void ResourceType_Mismatch_Rejects()
    {
        var f = AllFilter("create") with { ResourceTypes = new() { "user" } };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Ticket)).ShouldBeFalse();
    }

    // ==================== Schema shortname (content-only) ====================

    [Fact]
    public void ContentWithoutMatchingSchema_Rejected()
    {
        var f = AllFilter("create") with { SchemaShortnames = new() { "widget" } };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Content, schema: "gadget"))
            .ShouldBeFalse();
    }

    [Fact]
    public void ContentWithMatchingSchema_Accepted()
    {
        var f = AllFilter("create") with { SchemaShortnames = new() { "widget" } };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Content, schema: "widget"))
            .ShouldBeTrue();
    }

    [Fact]
    public void NonContent_Ignores_Schema_Filter()
    {
        // Only Content resources are gated by schema_shortname; Ticket with a
        // narrow filter list still matches because the predicate skips the check.
        var f = AllFilter("create") with { SchemaShortnames = new() { "widget" } };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Ticket, schema: "gadget"))
            .ShouldBeTrue();
    }
}
