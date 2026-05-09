using System.Collections.Generic;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit coverage for the per-permission filter_fields_values merge that
// QueryService injects into q.Search before SQL is built. Mirrors
// dmart Python's adapter.py:1529-1566 — every test below maps to a
// branch in that block. The pure-static MergeFilterFieldsValues
// is exercised directly so no DB or service graph is required.
public class FilterFieldsValuesMergeTests
{
    // ── Helpers ────────────────────────────────────────────────────────

    private static Query BaseQuery(string subpath = "/users", string? search = null,
        List<ResourceType>? filterTypes = null) =>
        new()
        {
            Type = QueryType.Subpath,
            SpaceName = "acme",
            Subpath = subpath,
            Search = search,
            FilterTypes = filterTypes,
        };

    private static Dictionary<string, object> Perms(params (string Key, string? Ffv)[] entries)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, ffv) in entries)
        {
            result[key] = new Dictionary<string, object>
            {
                ["allowed_actions"] = new List<string> { "query", "view" },
                ["conditions"] = new List<string>(),
                ["restricted_fields"] = new List<string>(),
                ["allowed_fields_values"] = new Dictionary<string, object>(),
                ["filter_fields_values"] = (object?)ffv ?? "",
            };
        }
        return result;
    }

    // Round-trips a perms dict through JSON to simulate what the
    // userpermissionscache JSONB column hands back — every nested dict
    // becomes a JsonElement, every leaf "filter_fields_values" string
    // becomes JsonValueKind.String. We need to handle this shape too.
    private static Dictionary<string, object> PermsAsJsonElements(params (string Key, string? Ffv)[] entries)
    {
        var raw = Perms(entries);
        var json = JsonSerializer.Serialize(raw);
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.Clone();
        return result;
    }

    // ── DedupeSearchTokens ─────────────────────────────────────────────

    [Fact]
    public void Dedupe_NullSearch_ReturnsUnchanged()
    {
        var q = BaseQuery(search: null);
        var result = QueryService.DedupeSearchTokens(q);
        result.Search.ShouldBeNull();
    }

    [Fact]
    public void Dedupe_EmptySearch_ReturnsUnchanged()
    {
        var q = BaseQuery(search: "");
        var result = QueryService.DedupeSearchTokens(q);
        // Empty stays empty — we early-return before allocating a new record.
        result.Search.ShouldBe("");
    }

    [Fact]
    public void Dedupe_NoDuplicates_ReturnsSameInstance()
    {
        var q = BaseQuery(search: "@status:open @kind:bug");
        var result = QueryService.DedupeSearchTokens(q);
        // No work done — same record reference, untouched.
        result.ShouldBeSameAs(q);
    }

    [Fact]
    public void Dedupe_RepeatedTokens_AreCollapsed()
    {
        var q = BaseQuery(search: "@status:open @status:open @kind:bug");
        var result = QueryService.DedupeSearchTokens(q);
        result.Search.ShouldBe("@status:open @kind:bug");
    }

    [Fact]
    public void Dedupe_NormalizesWhitespace_WhenDupesPresent()
    {
        // Dedupe only allocates a new search string when at least one
        // duplicate was dropped. When that path runs, the rebuild emits
        // single-space-separated tokens regardless of the input spacing.
        var q = BaseQuery(search: "  @status:open    @status:open    @kind:bug   ");
        var result = QueryService.DedupeSearchTokens(q);
        result.Search.ShouldBe("@status:open @kind:bug");
    }

    [Fact]
    public void Dedupe_PreservesInputVerbatim_WhenNoDupes()
    {
        // No duplicates → early-return the original record unchanged,
        // including any irregular whitespace the caller passed in.
        var q = BaseQuery(search: "  @status:open    @kind:bug   ");
        var result = QueryService.DedupeSearchTokens(q);
        result.ShouldBeSameAs(q);
    }

    // ── ExtractFilterFieldsValues ──────────────────────────────────────

    [Fact]
    public void Extract_FromDictionary_StringValue()
    {
        var entry = new Dictionary<string, object> { ["filter_fields_values"] = "@status:open" };
        QueryService.ExtractFilterFieldsValues(entry).ShouldBe("@status:open");
    }

    [Fact]
    public void Extract_FromDictionary_EmptyString_ReturnsNull()
    {
        var entry = new Dictionary<string, object> { ["filter_fields_values"] = "" };
        QueryService.ExtractFilterFieldsValues(entry).ShouldBeNull();
    }

    [Fact]
    public void Extract_FromDictionary_MissingKey_ReturnsNull()
    {
        var entry = new Dictionary<string, object> { ["allowed_actions"] = new List<string>() };
        QueryService.ExtractFilterFieldsValues(entry).ShouldBeNull();
    }

    [Fact]
    public void Extract_FromDictionary_JsonElementValue()
    {
        // Half-deserialized form: outer is Dictionary<string,object>,
        // but "filter_fields_values" came back as a JsonElement.String.
        using var doc = JsonDocument.Parse("\"@status:open\"");
        var entry = new Dictionary<string, object> { ["filter_fields_values"] = doc.RootElement.Clone() };
        QueryService.ExtractFilterFieldsValues(entry).ShouldBe("@status:open");
    }

    [Fact]
    public void Extract_FromJsonElement_ObjectShape()
    {
        // Fully cached form: the entry itself is a JsonElement.Object.
        using var doc = JsonDocument.Parse("""{"filter_fields_values":"@kind:bug","conditions":[]}""");
        QueryService.ExtractFilterFieldsValues(doc.RootElement.Clone())
            .ShouldBe("@kind:bug");
    }

    [Fact]
    public void Extract_FromJsonElement_EmptyString_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"filter_fields_values":""}""");
        QueryService.ExtractFilterFieldsValues(doc.RootElement.Clone()).ShouldBeNull();
    }

    [Fact]
    public void Extract_FromUnknownType_ReturnsNull()
    {
        QueryService.ExtractFilterFieldsValues(42).ShouldBeNull();
    }

    // ── MergeFilterFieldsValues — short-circuits ───────────────────────

    [Fact]
    public void Merge_EmptyPermissions_ReturnsQueryUnchanged()
    {
        var q = BaseQuery(search: "@x:y");
        var result = QueryService.MergeFilterFieldsValues(q, new() { "acme:users:user:*" }, new());
        result.Search.ShouldBe("@x:y");
    }

    [Fact]
    public void Merge_NoMatchingPolicy_ReturnsQueryUnchanged()
    {
        // perm_key is for a different space, so no policy starts with it.
        var perms = Perms(("other:users:user", "@status:active"));
        var q = BaseQuery();
        var result = QueryService.MergeFilterFieldsValues(q, new() { "acme:users:user:*" }, perms);
        result.Search.ShouldBeNull();
    }

    [Fact]
    public void Merge_PermissionWithoutFfv_ReturnsQueryUnchanged()
    {
        var perms = Perms(("acme:users:user", null));
        var q = BaseQuery();
        var result = QueryService.MergeFilterFieldsValues(q, new() { "acme:users:user:*" }, perms);
        result.Search.ShouldBeNull();
    }

    [Fact]
    public void Merge_PermissionWithEmptyFfv_ReturnsQueryUnchanged()
    {
        var perms = Perms(("acme:users:user", ""));
        var q = BaseQuery();
        var result = QueryService.MergeFilterFieldsValues(q, new() { "acme:users:user:*" }, perms);
        result.Search.ShouldBeNull();
    }

    [Fact]
    public void Merge_NoFilteredPolicies_ReturnsQueryUnchanged()
    {
        // policies don't share the q.SpaceName:subpath prefix, so
        // filtered_policies is empty and the merge no-ops.
        var perms = Perms(("acme:users:user", "@status:active"));
        var q = BaseQuery(subpath: "/orders");
        var result = QueryService.MergeFilterFieldsValues(q, new() { "acme:users:user:*" }, perms);
        result.Search.ShouldBeNull();
    }

    // ── MergeFilterFieldsValues — happy paths ──────────────────────────

    [Fact]
    public void Merge_SinglePermission_BuildsExpectedClause()
    {
        // Single FFV permission: the merged search carries the
        // @space_name / @subpath / @resource_type triple plus the FFV body.
        var perms = Perms(("acme:users:user", "@status:active"));
        var q = BaseQuery();
        var result = QueryService.MergeFilterFieldsValues(
            q, new() { "acme:users:user:true:*" }, perms);

        result.Search.ShouldNotBeNull();
        result.Search.ShouldContain("@space_name:acme");
        result.Search.ShouldContain("@subpath:/users");
        result.Search.ShouldContain("@resource_type:user");
        result.Search.ShouldContain("@status:active");
    }

    [Fact]
    public void Merge_PreservesExistingSearch_AndAppendsClause()
    {
        var perms = Perms(("acme:users:user", "@status:active"));
        var q = BaseQuery(search: "@displayname:alice");
        var result = QueryService.MergeFilterFieldsValues(
            q, new() { "acme:users:user:*" }, perms);

        result.Search.ShouldStartWith("@displayname:alice ");
        result.Search.ShouldContain("@status:active");
    }

    [Fact]
    public void Merge_MultiplePermissions_CombinesIntoPipeAlternation()
    {
        // Two perms targeting the SAME subpath but different
        // resource_types both pass the `acme:users` startsWith filter,
        // so the merge OR-joins them:
        //   @space_name:acme|acme  @subpath:/users|/users
        //   @resource_type:user|role  <ffv-1> <ffv-2>
        // ffv_query stays distinct in append-order.
        var perms = Perms(
            ("acme:users:user", "@status:active"),
            ("acme:users:role", "@kind:admin"));
        var policies = new List<string> { "acme:users:user:*", "acme:users:role:*" };

        var q = BaseQuery(); // subpath = /users
        var result = QueryService.MergeFilterFieldsValues(q, policies, perms);

        result.Search.ShouldNotBeNull();
        result.Search.ShouldContain("@space_name:acme|acme");
        result.Search.ShouldContain("@subpath:/users|/users");
        result.Search.ShouldContain("@resource_type:user|role");
        result.Search.ShouldContain("@status:active");
        result.Search.ShouldContain("@kind:admin");
    }

    [Fact]
    public void Merge_DuplicateFfvAcrossPermissions_AppearsOnce()
    {
        // Two perm_keys carry the SAME ffv string — Python's `if ffv not
        // in ffv_query` dedupes. Verify the body shows up once even
        // though the @space_name/@subpath/@resource_type lists still
        // carry both perms (one entry each).
        var perms = Perms(
            ("acme:users:user", "@status:active"),
            ("acme:users:role", "@status:active"));
        var policies = new List<string> { "acme:users:user:*", "acme:users:role:*" };
        var q = BaseQuery();

        var result = QueryService.MergeFilterFieldsValues(q, policies, perms);

        result.Search.ShouldNotBeNull();
        // Count occurrences of the ffv body — must be exactly 1.
        var occurrences = 0;
        var idx = 0;
        while ((idx = result.Search!.IndexOf("@status:active", idx, System.StringComparison.Ordinal)) >= 0)
        { occurrences++; idx += "@status:active".Length; }
        occurrences.ShouldBe(1);

        // But both perm_keys contributed to the per-key triple lists.
        result.Search.ShouldContain("@space_name:acme|acme");
        result.Search.ShouldContain("@resource_type:user|role");
    }

    [Fact]
    public void Merge_DedupesAdjacentTokensInFinalSearch()
    {
        // Caller-provided search already contains @space_name:acme; the
        // merge appends the same token. The post-merge dedupe collapses it.
        var perms = Perms(("acme:users:user", "@status:active"));
        var q = BaseQuery(search: "@space_name:acme");
        var result = QueryService.MergeFilterFieldsValues(
            q, new() { "acme:users:user:*" }, perms);

        result.Search.ShouldNotBeNull();
        // Exactly one occurrence after dedupe.
        var occurrences = 0;
        var idx = 0;
        while ((idx = result.Search!.IndexOf("@space_name:acme", idx, System.StringComparison.Ordinal)) >= 0)
        { occurrences++; idx += "@space_name:acme".Length; }
        occurrences.ShouldBe(1);
    }

    [Fact]
    public void Merge_RootSubpath_UsesSlashTarget()
    {
        // q.Subpath="/" → subpathTarget="/" so policies like
        // "acme:/:user:*" still pass the StartsWith("acme:/") filter.
        var perms = Perms(("acme:/:user", "@status:active"));
        var q = new Query { Type = QueryType.Subpath, SpaceName = "acme", Subpath = "/" };
        var result = QueryService.MergeFilterFieldsValues(
            q, new() { "acme:/:user:*" }, perms);

        result.Search.ShouldNotBeNull();
        result.Search.ShouldContain("@space_name:acme");
        result.Search.ShouldContain("@status:active");
    }

    [Fact]
    public void Merge_PermKeyWithLeadingSlash_StillMatches_PolicyWithoutSlash()
    {
        // Regression: GenerateUserPermissionsAsync stores subpath verbatim
        // from `permissions.subpaths`, while BuildUserQueryPoliciesAsync
        // emits policies with the subpath TrimStart('/')'d. Without the
        // dual-form match the FFV silently never fires for any permission
        // whose stored subpath has a leading slash — the common storage
        // convention for csdmart, caught by the FFV→SQL integration test.
        var perms = Perms(("ffv_space:/sp_xx:content", "@state:active"));
        var q = new Query { Type = QueryType.Subpath, SpaceName = "ffv_space", Subpath = "/sp_xx" };
        var result = QueryService.MergeFilterFieldsValues(
            q, new() { "ffv_space:sp_xx:content:*" }, perms);

        result.Search.ShouldNotBeNull();
        result.Search.ShouldContain("@state:active");
        // The emitted @subpath clause uses the normalised (slash-stripped)
        // form so the SQL parser sees a single canonical leading slash.
        result.Search.ShouldContain("@subpath:/sp_xx");
    }

    // ── filter_types narrowing ─────────────────────────────────────────

    [Fact]
    public void Merge_FilterTypes_OnlyLastFilterTypeSurvives()
    {
        // Python's loop bug: filtered_policies is REASSIGNED on each ft,
        // not appended. We mirror that — only the last filter_type's
        // matches contribute. Verify by setting two filter_types where
        // only the second's perm has a usable FFV against the policy list.
        var perms = Perms(
            ("acme:users:user",   "@status:active"),
            ("acme:users:role",   "@kind:admin"));
        var policies = new List<string>
        {
            "acme:users:user:*",
            "acme:users:role:*",
        };

        // Order matters: User first, then Role — only Role survives.
        var q = BaseQuery(filterTypes: new List<ResourceType> { ResourceType.User, ResourceType.Role });
        var result = QueryService.MergeFilterFieldsValues(q, policies, perms);

        result.Search.ShouldNotBeNull();
        result.Search.ShouldContain("@kind:admin");
        result.Search.ShouldNotContain("@status:active");
    }

    [Fact]
    public void Merge_FilterTypes_NarrowsToMatchingResourceType()
    {
        // filter_types=[User] → only the user-typed perm contributes.
        var perms = Perms(
            ("acme:users:user",    "@status:active"),
            ("acme:users:content", "@hidden:false"));
        var policies = new List<string>
        {
            "acme:users:user:*",
            "acme:users:content:*",
        };

        var q = BaseQuery(filterTypes: new List<ResourceType> { ResourceType.User });
        var result = QueryService.MergeFilterFieldsValues(q, policies, perms);

        result.Search.ShouldNotBeNull();
        result.Search.ShouldContain("@status:active");
        result.Search.ShouldNotContain("@hidden:false");
    }

    // ── JsonElement-shaped permissions (cache round-trip) ──────────────

    [Fact]
    public void Merge_PermissionsAsJsonElements_StillProducesClause()
    {
        // After the userpermissionscache round-trip, the inner dicts
        // come back as JsonElement.Object, not Dictionary<string,object>.
        // The merge must still reach the FFV string via ExtractFilterFieldsValues.
        var perms = PermsAsJsonElements(("acme:users:user", "@status:active"));
        var q = BaseQuery();
        var result = QueryService.MergeFilterFieldsValues(
            q, new() { "acme:users:user:*" }, perms);

        result.Search.ShouldNotBeNull();
        result.Search.ShouldContain("@status:active");
        result.Search.ShouldContain("@space_name:acme");
    }
}
