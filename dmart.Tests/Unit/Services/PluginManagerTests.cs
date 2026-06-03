using System.Collections.Generic;
using System.Text;
using Dmart.Config;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for the pure filter-matching logic in PluginManager. Covers the
// permission-style filter shape: subpaths is a dict keyed by space, with
// __all_spaces__ / __all_subpaths__ / __current_user__ sentinels, and empty
// lists for resource_types / schema_shortnames / actions mean "match all".
// Legacy flat-array `subpaths` is detected and rejected by HasLegacySubpathsShape.
public class PluginManagerTests
{
    // Helper: builds the "match everything" filter (the most-permissive
    // equivalent of the old { __ALL__, __ALL__, __ALL__ } setup).
    private static EventFilter AllFilter(params string[] actions) => new()
    {
        Subpaths = new() { ["__all_spaces__"] = new() { "__all_subpaths__" } },
        ResourceTypes = new(),       // empty = all
        SchemaShortnames = new(),    // empty = all
        Actions = new(actions),
    };

    private static Event Evt(string subpath, ResourceType? type = null, string? schema = null,
        string space = "myspace", string user = "tester") => new()
    {
        SpaceName = space,
        Subpath = subpath,
        Shortname = "foo",
        ActionType = ActionType.Create,
        ResourceType = type,
        SchemaShortname = schema,
        UserShortname = user,
    };

    // ==================== Subpath matching ====================

    [Fact]
    public void AllSpaces_AllSubpaths_Matches_Everything()
    {
        PluginManager.MatchedFilters(AllFilter("create"), Evt("/any/thing"))
            .ShouldBeTrue();
    }

    [Fact]
    public void Per_Space_Filter_Matches_Only_That_Space()
    {
        var f = AllFilter("create") with
        {
            Subpaths = new() { ["myspace"] = new() { "__all_subpaths__" } },
        };
        PluginManager.MatchedFilters(f, Evt("/x", space: "myspace")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("/x", space: "otherspace")).ShouldBeFalse();
    }

    [Fact]
    public void Literal_Subpath_Matches_Exact_And_Children()
    {
        var f = AllFilter("create") with
        {
            Subpaths = new() { ["__all_spaces__"] = new() { "users" } },
        };
        // Exact match, with and without leading slash on the event.
        PluginManager.MatchedFilters(f, Evt("users")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("/users")).ShouldBeTrue();
        // Hierarchical startswith — children match.
        PluginManager.MatchedFilters(f, Evt("users/alice")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("users/alice/profile")).ShouldBeTrue();
        // Prefix-confusable name must NOT match.
        PluginManager.MatchedFilters(f, Evt("usersearch")).ShouldBeFalse();
    }

    [Fact]
    public void CurrentUser_Sentinel_Resolves_To_Event_User()
    {
        // Mirrors the permission engine's __current_user__ substitution: the
        // sentinel is replaced by the event's user_shortname before matching,
        // so a plugin can scope to a user's own subtree.
        var f = AllFilter("create") with
        {
            Subpaths = new() { ["__all_spaces__"] = new() { "users/__current_user__" } },
        };
        PluginManager.MatchedFilters(f, Evt("users/alice", user: "alice")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("users/bob", user: "alice")).ShouldBeFalse();
    }

    [Fact]
    public void Per_Space_Beats_AllSpaces_Independent_Of_Order()
    {
        // Both entries cover the event; either match path should succeed.
        var f = AllFilter("create") with
        {
            Subpaths = new()
            {
                ["myspace"] = new() { "tickets" },
                ["__all_spaces__"] = new() { "shared" },
            },
        };
        PluginManager.MatchedFilters(f, Evt("tickets/open", space: "myspace")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("shared/x", space: "anyspace")).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("other", space: "myspace")).ShouldBeFalse();
    }

    [Fact]
    public void Empty_Subpaths_Dict_Matches_Nothing()
    {
        // Mirrors the permission engine: an empty subpaths dict means the
        // plugin doesn't fire on any event. Authors must explicitly opt
        // in to "everything" via { __all_spaces__: [__all_subpaths__] }.
        var f = new EventFilter
        {
            Subpaths = new(),
            Actions = new() { "create" },
        };
        PluginManager.MatchedFilters(f, Evt("/x")).ShouldBeFalse();
    }

    // ==================== Resource type matching ====================

    [Fact]
    public void Empty_ResourceTypes_Matches_All()
    {
        // Empty list = unconstrained, matching permissions' convention.
        var f = AllFilter("create");
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Ticket)).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Content)).ShouldBeTrue();
    }

    [Fact]
    public void ResourceType_Specific_Match()
    {
        var f = AllFilter("create") with { ResourceTypes = new() { "user" } };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.User)).ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Ticket)).ShouldBeFalse();
    }

    // ==================== Schema shortname (content-only) ====================

    [Fact]
    public void Empty_SchemaShortnames_Matches_All_Schemas()
    {
        var f = AllFilter("create");
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Content, schema: "anything"))
            .ShouldBeTrue();
    }

    [Fact]
    public void Content_With_Schema_Filter_Gates_On_Schema()
    {
        var f = AllFilter("create") with { SchemaShortnames = new() { "widget" } };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Content, schema: "widget"))
            .ShouldBeTrue();
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Content, schema: "gadget"))
            .ShouldBeFalse();
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

    // ==================== Legacy-shape detection ====================

    [Fact]
    public void HasLegacySubpathsShape_Detects_Old_Flat_Array()
    {
        var legacy = Encoding.UTF8.GetBytes(
            "{ \"shortname\":\"x\", \"filters\": { \"subpaths\": [\"__ALL__\"], \"actions\": [\"create\"] } }");
        PluginManager.HasLegacySubpathsShape(legacy).ShouldBeTrue();
    }

    [Fact]
    public void HasLegacySubpathsShape_Accepts_New_Dict_Shape()
    {
        var modern = Encoding.UTF8.GetBytes(
            "{ \"shortname\":\"x\", \"filters\": { \"subpaths\": { \"__all_spaces__\": [\"__all_subpaths__\"] } } }");
        PluginManager.HasLegacySubpathsShape(modern).ShouldBeFalse();
    }

    [Fact]
    public void HasLegacySubpathsShape_Tolerates_Missing_Filter_Block()
    {
        // API-only plugins ship without `filters`; the probe must NOT
        // mis-classify them as legacy.
        var apiOnly = Encoding.UTF8.GetBytes("{ \"shortname\":\"api\", \"type\":\"api\" }");
        PluginManager.HasLegacySubpathsShape(apiOnly).ShouldBeFalse();
    }

    // ==================== Null filter members (deserialized null) ====================
    // EventFilter's list/dict members carry default initializers, but a config.json
    // with a present-but-null key ("schema_shortnames": null) makes System.Text.Json
    // source-gen overwrite that default with null. Matching/registration must treat
    // null exactly like the empty default — "match all" — rather than throw an NRE.

    [Fact]
    public void Null_SchemaShortnames_Matches_All_Schemas()
    {
        var f = AllFilter("create") with { SchemaShortnames = null! };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Content, schema: "anything"))
            .ShouldBeTrue();
    }

    [Fact]
    public void Null_ResourceTypes_Matches_All()
    {
        var f = AllFilter("create") with { ResourceTypes = null! };
        PluginManager.MatchedFilters(f, Evt("/", ResourceType.Ticket)).ShouldBeTrue();
    }

    [Fact]
    public void Null_Subpaths_Matches_Nothing()
    {
        // Mirrors Empty_Subpaths_Dict_Matches_Nothing: a null subpaths dict is
        // "no opt-in ⇒ no match", not an NRE.
        var f = AllFilter("create") with { Subpaths = null! };
        PluginManager.MatchedFilters(f, Evt("/x")).ShouldBeFalse();
    }

    [Fact]
    public void Register_NullActions_DoesNotThrow_And_Registers_Hook()
    {
        // The registration path (PluginManager.Register) reads Filters.Actions to
        // decide which ActionType buckets the hook subscribes to. A null Actions
        // (from "actions": null in config.json) must mean "every action", same as
        // the empty-list default. Register() has no per-wrapper try/catch, so an
        // NRE here would abort registration of every remaining plugin — this pins
        // that it doesn't.
        var pm = new PluginManager(
            new IHookPlugin[] { new NoopHook() },
            Array.Empty<IApiPlugin>(),
            new SpaceEventLogger(Options.Create(new DmartSettings()),
                NullLogger<SpaceEventLogger>.Instance),
            NullLogger<PluginManager>.Instance);

        var wrapper = new PluginWrapper
        {
            Shortname = "noop_hook",
            IsActive = true,
            Type = PluginType.Hook,
            ListenTime = EventListenTime.Before,
            Filters = AllFilter() with { Actions = null! },
        };

        Should.NotThrow(() => pm.Register(new[] { wrapper }));
        pm.ActivePlugins.ShouldContain("noop_hook");
    }

    // Minimal hook stub so Register can resolve a C# instance for the wrapper's
    // shortname without touching the filesystem or DI container.
    private sealed class NoopHook : IHookPlugin
    {
        public string Shortname => "noop_hook";
        public Task HookAsync(Event e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
