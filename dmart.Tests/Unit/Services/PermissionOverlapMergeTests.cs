using System.Collections.Generic;
using Dmart.DataAdapters.Sql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit coverage for AccessRepository's overlapping-permission merge helpers —
// the additive combine applied when two permissions a user holds resolve to the
// SAME space:subpath:resource_type key in GenerateUserPermissionsAsync. Grants
// widen (union); restrictions bind only where every overlapping permission
// agrees (an unrestricted permission grants the broader access and wins). Pure
// statics, so no DB or service graph is required.
public class PermissionOverlapMergeTests
{
    // ── allowed_actions / conditions: grant union ───────────────────────────
    [Fact]
    public void UnionStrings_unions_and_dedupes_preserving_order()
        => AccessRepository.UnionStrings(
                new List<string> { "query", "view" },
                new List<string> { "view", "update" })
            .ShouldBe(new[] { "query", "view", "update" });

    [Fact]
    public void UnionStrings_treats_null_as_empty()
    {
        AccessRepository.UnionStrings(null, new List<string> { "query" }).ShouldBe(new[] { "query" });
        AccessRepository.UnionStrings(new List<string> { "query" }, null).ShouldBe(new[] { "query" });
        AccessRepository.UnionStrings(null, null).ShouldBeEmpty();
    }

    // ── restricted_fields: restriction intersection (unrestricted wins) ─────
    [Fact]
    public void IntersectStrings_keeps_only_fields_both_restrict()
        => AccessRepository.IntersectStrings(
                new List<string> { "a", "b", "c" },
                new List<string> { "b", "c", "d" })
            .ShouldBe(new[] { "b", "c" });

    [Fact]
    public void IntersectStrings_empty_or_null_side_restricts_nothing()
    {
        // One permission restricts nothing → the user may write those fields
        // through that permission, so the combined restriction is empty.
        AccessRepository.IntersectStrings(new List<string> { "a" }, new List<string>()).ShouldBeEmpty();
        AccessRepository.IntersectStrings(new List<string> { "a" }, null).ShouldBeEmpty();
        AccessRepository.IntersectStrings(null, null).ShouldBeEmpty();
    }

    // ── filter_fields_values: empty allows all rows, else OR ────────────────
    [Fact]
    public void CombineFilterFieldsValues_empty_side_allows_all_rows()
    {
        AccessRepository.CombineFilterFieldsValues("@status:active", "").ShouldBe("");
        AccessRepository.CombineFilterFieldsValues("", "@status:active").ShouldBe("");
        AccessRepository.CombineFilterFieldsValues(null, "@status:active").ShouldBe("");
    }

    [Fact]
    public void CombineFilterFieldsValues_equal_filters_collapse()
        => AccessRepository.CombineFilterFieldsValues("@status:active", "@status:active")
            .ShouldBe("@status:active");

    [Fact]
    public void CombineFilterFieldsValues_distinct_filters_OR_together()
        => AccessRepository.CombineFilterFieldsValues("@status:active", "@type:public")
            .ShouldBe("(@status:active)|(@type:public)");

    // ── allowed_fields_values: empty allows any value, else union fields ────
    [Fact]
    public void CombineAllowedFieldsValues_empty_side_allows_any_value()
    {
        var capped = new Dictionary<string, object> { ["status"] = "active" };
        AccessRepository.CombineAllowedFieldsValues(capped, new Dictionary<string, object>()).ShouldBeEmpty();
        AccessRepository.CombineAllowedFieldsValues(capped, null).ShouldBeEmpty();
        AccessRepository.CombineAllowedFieldsValues(null, capped).ShouldBeEmpty();
    }

    [Fact]
    public void CombineAllowedFieldsValues_both_capped_unions_fields()
    {
        var a = new Dictionary<string, object> { ["status"] = "active" };
        var b = new Dictionary<string, object> { ["type"] = "public" };
        AccessRepository.CombineAllowedFieldsValues(a, b)
            .Keys.ShouldBe(new[] { "status", "type" }, ignoreOrder: true);
    }
}
