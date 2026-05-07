using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for QueryService.TryParseEventLine — drives Python's events_query
// semantics directly (line in, (timestamp, Record) out). Python applies ONLY
// `from_date`, `to_date`, and the substring `search`; `filter_shortnames`,
// `filter_types`, `subpath`, and `exact_subpath` are ignored on the events
// path because events live at the space root with no subpath bucketing.
//
// Test lines are written in the same shape SpaceEventLogger emits so the
// tests double as a cross-check that writer and reader agree on the format.
public class QueryEventsTests
{
    private static string BuildLine(string ts = "2026-05-06T10:00:00.000000",
        string subpath = "/users",
        string shortname = "alice",
        string action = "create",
        string? resourceType = "user",
        string user = "tester")
    {
        // Hand-build to keep the test in lockstep with the Python-shaped
        // on-disk format (resource block + request key).
        var typeKv = resourceType is null ? "\"type\":null" : $"\"type\":\"{resourceType}\"";
        var resource = $"\"resource\":{{{typeKv},\"space_name\":\"myspace\","
                     + $"\"subpath\":\"{subpath}\",\"shortname\":\"{shortname}\"}}";
        return $"{{{resource},\"user_shortname\":\"{user}\","
             + $"\"request\":\"{action}\",\"timestamp\":\"{ts}\",\"attributes\":{{}}}}";
    }

    // Legacy flat-shape line — exercised so we can prove the reader still
    // handles logs written by the prior revision of SpaceEventLogger.
    private static string BuildLegacyLine(string ts = "2026-05-06T10:00:00.000",
        string subpath = "/users", string shortname = "alice",
        string action = "create", string resourceType = "user", string user = "tester")
        => $"{{\"timestamp\":\"{ts}\",\"space_name\":\"myspace\",\"subpath\":\"{subpath}\","
         + $"\"shortname\":\"{shortname}\",\"action_type\":\"{action}\","
         + $"\"resource_type\":\"{resourceType}\","
         + $"\"user_shortname\":\"{user}\",\"attributes\":{{}}}}";

    private static Query Q(DateTime? from = null, DateTime? to = null, string? search = null)
        => new()
        {
            Type = QueryType.Events,
            SpaceName = "myspace",
            Subpath = "/",
            FromDate = from,
            ToDate = to,
            Search = search,
        };

    [Fact]
    public void Parses_Wellformed_Line_Into_Record_With_Whole_Json_As_Attributes()
    {
        var line = BuildLine();
        QueryService.TryParseEventLine(line, Q(), out var ts, out var rec).ShouldBeTrue();
        ts.ShouldBe(new DateTime(2026, 5, 6, 10, 0, 0));
        rec.ShouldNotBeNull();
        rec!.Subpath.ShouldBe("users");                  // Record normalizes leading slash off
        rec.Shortname.ShouldBe("alice");                 // from resource.shortname
        rec.ResourceType.ShouldBe(ResourceType.User);    // from resource.type

        // Python parity: attributes is the WHOLE event JSON, not curated.
        rec.Attributes.ShouldNotBeNull();
        rec.Attributes!.ShouldContainKey("resource");
        rec.Attributes.ShouldContainKey("user_shortname");
        rec.Attributes.ShouldContainKey("request");
        rec.Attributes.ShouldContainKey("timestamp");
        rec.Attributes.ShouldContainKey("attributes");

        // Drill into resource sub-object — must round-trip the inner Locator.
        var resource = (JsonElement)rec.Attributes["resource"];
        resource.GetProperty("space_name").GetString().ShouldBe("myspace");
        resource.GetProperty("subpath").GetString().ShouldBe("/users");
        resource.GetProperty("shortname").GetString().ShouldBe("alice");
        resource.GetProperty("type").GetString().ShouldBe("user");
    }

    [Fact]
    public void Parses_Legacy_Flat_Line_For_Backwards_Compatibility()
    {
        var line = BuildLegacyLine();
        QueryService.TryParseEventLine(line, Q(), out var ts, out var rec).ShouldBeTrue();
        ts.ShouldBe(new DateTime(2026, 5, 6, 10, 0, 0));
        rec.ShouldNotBeNull();
        rec!.Subpath.ShouldBe("users");
        rec.Shortname.ShouldBe("alice");
        rec.ResourceType.ShouldBe(ResourceType.User);
    }

    [Fact]
    public void Malformed_Json_Returns_False()
    {
        QueryService.TryParseEventLine("not-json{", Q(), out _, out var rec).ShouldBeFalse();
        rec.ShouldBeNull();
    }

    [Fact]
    public void Empty_Object_Parses_With_Fallback_Defaults()
    {
        // Edge case: an empty {} line still parses successfully; resource_type
        // falls back to Content, shortname falls back to "_". Python's
        // events_query crashes hard on this — we degrade more gracefully so a
        // tail of a partially-written line doesn't poison the whole response.
        QueryService.TryParseEventLine("{}", Q(), out _, out var rec).ShouldBeTrue();
        rec!.Shortname.ShouldBe("_");
        rec.ResourceType.ShouldBe(ResourceType.Content);
    }

    [Fact]
    public void Date_Window_Includes_Range_And_Excludes_Outside()
    {
        var insideLine = BuildLine(ts: "2026-05-06T10:00:00.000000");
        var beforeLine = BuildLine(ts: "2026-04-01T00:00:00.000000");
        var afterLine  = BuildLine(ts: "2026-06-01T00:00:00.000000");
        var q = Q(from: new DateTime(2026, 5, 1), to: new DateTime(2026, 5, 31));
        QueryService.TryParseEventLine(insideLine, q, out _, out _).ShouldBeTrue();
        QueryService.TryParseEventLine(beforeLine, q, out _, out _).ShouldBeFalse();
        QueryService.TryParseEventLine(afterLine,  q, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Search_Substring_Filter_Honored()
    {
        var aliceLine = BuildLine(shortname: "alice");
        var bobLine   = BuildLine(shortname: "bob");
        var q = Q(search: "alice");
        QueryService.TryParseEventLine(aliceLine, q, out _, out _).ShouldBeTrue();
        QueryService.TryParseEventLine(bobLine,   q, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void FilterShortnames_Is_Ignored_For_Events_Python_Parity()
    {
        // Python's events_query does NOT honor filter_shortnames — make sure
        // we don't drop a Bob line just because the caller put "alice" in
        // the allow-list.
        var bobLine = BuildLine(shortname: "bob");
        var q = new Query
        {
            Type = QueryType.Events,
            SpaceName = "myspace",
            Subpath = "/",
            FilterShortnames = new() { "alice" },
        };
        QueryService.TryParseEventLine(bobLine, q, out _, out var rec).ShouldBeTrue();
        rec.ShouldNotBeNull();
    }

    [Fact]
    public void FilterTypes_Is_Ignored_For_Events_Python_Parity()
    {
        // Same parity rule — Python doesn't filter events by resource type.
        var ticketLine = BuildLine(resourceType: "ticket");
        var q = new Query
        {
            Type = QueryType.Events,
            SpaceName = "myspace",
            Subpath = "/",
            FilterTypes = new() { ResourceType.User },
        };
        QueryService.TryParseEventLine(ticketLine, q, out _, out var rec).ShouldBeTrue();
        rec.ShouldNotBeNull();
        rec!.ResourceType.ShouldBe(ResourceType.Ticket);
    }

    [Fact]
    public void Subpath_Filter_Is_Ignored_For_Events_Python_Parity()
    {
        // Events live at the space root; Python doesn't filter the events
        // feed by subpath. A query with subpath=/groups must still surface
        // events whose resource.subpath is /users.
        var line = BuildLine(subpath: "/users");
        var q = new Query
        {
            Type = QueryType.Events,
            SpaceName = "myspace",
            Subpath = "/groups",
            ExactSubpath = true,
        };
        QueryService.TryParseEventLine(line, q, out _, out var rec).ShouldBeTrue();
        rec.ShouldNotBeNull();
    }
}
