using System.Linq;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Regression tests for the /managed/query type=spaces path. dmart Python's
// QueryType.spaces goes through SpaceRepository + per-row permission filtering
// — the C# port previously routed every query type to EntryRepository, which
// silently returned an unrelated result set. These tests pin the corrected
// behavior so it can't regress.
public class QuerySpacesTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public QuerySpacesTests(DmartFactory factory) => _factory = factory;

    private (QueryService query, SpaceRepository spaces) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<QueryService>(),
            sp.GetRequiredService<SpaceRepository>());
    }

    // ==================== happy path ====================

    [Fact]
    public async Task QuerySpaces_ReturnsSpaceRowsNotEntries()
    {
        if (!DmartFactory.HasPg) return;
        var (query, spaces) = Resolve();

        var q = new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 100,
        };

        var resp = await query.ExecuteAsync(q, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();

        // Every returned record must have resource_type == space — if the old
        // bug comes back and the call routes to EntryRepository, we'd see
        // content/schema/folder records here and the assertion would catch it.
        foreach (var rec in resp.Records!)
            rec.ResourceType.ShouldBe(ResourceType.Space);

        // The admin should see at least the "management" space (always present
        // after AdminBootstrap runs). We don't assert the exact count because
        // the live DB's space list varies.
        resp.Records!.Select(r => r.Shortname).ShouldContain("management");
    }

    [Fact]
    public async Task QuerySpaces_AttributesIncludeSpaceSpecificFields()
    {
        if (!DmartFactory.HasPg) return;
        var (query, _) = Resolve();

        var q = new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 100,
        };

        var resp = await query.ExecuteAsync(q, _factory.AdminShortname);
        var management = resp.Records!.FirstOrDefault(r => r.Shortname == "management");
        management.ShouldNotBeNull();

        // Space-specific columns must appear in attributes so clients can see
        // them (matches Python's SQLModel.model_dump output).
        management!.Attributes.ShouldNotBeNull();
        management.Attributes!.ShouldContainKey("languages");
        management.Attributes.ShouldContainKey("indexing_enabled");
        // space_name is emitted by Python's to_record and by our SpaceMapper.
        management.Attributes.ShouldContainKey("space_name");
        // query_policies is explicitly stripped in _set_query_final_results —
        // it's an internal gating field that mustn't leak to clients.
        management.Attributes.ShouldNotContainKey("query_policies");
    }

    [Fact]
    public async Task QuerySpaces_TotalAttributeReflectsVisibleCount()
    {
        if (!DmartFactory.HasPg) return;
        var (query, _) = Resolve();

        var q = new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 100,
        };

        var resp = await query.ExecuteAsync(q, _factory.AdminShortname);
        resp.Attributes.ShouldNotBeNull();
        resp.Attributes!.ShouldContainKey("total");
        resp.Attributes["total"].ShouldBe(resp.Records!.Count);
    }

    // ==================== validation ====================

    [Fact]
    public async Task QuerySpaces_NonManagementSpace_Rejected()
    {
        if (!DmartFactory.HasPg) return;
        var (query, _) = Resolve();

        var q = new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "personal",
            Subpath = "/",
            Limit = 100,
        };

        var resp = await query.ExecuteAsync(q, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Failed);
        resp.Error!.Message.ShouldContain("management");
    }

    [Fact]
    public async Task QuerySpaces_NonRootSubpath_Rejected()
    {
        if (!DmartFactory.HasPg) return;
        var (query, _) = Resolve();

        var q = new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/users",
            Limit = 100,
        };

        var resp = await query.ExecuteAsync(q, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Failed);
    }

    // ==================== paging ====================

    [Fact]
    public async Task QuerySpaces_RespectsLimitAndOffset()
    {
        if (!DmartFactory.HasPg) return;
        var (query, _) = Resolve();

        // Ask for a limit of 2 — even if the live DB has more than 2 visible
        // spaces, we should get at most 2 records back. This catches the
        // regression where the paging happens on the wrong result set.
        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 2,
            Offset = 0,
        }, _factory.AdminShortname);

        resp.Status.ShouldBe(Status.Success);
        resp.Records!.Count.ShouldBeLessThanOrEqualTo(2);
    }
}
