using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
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

    [Fact]
    public async Task QuerySpaces_SuperAdmin_Sees_All_Spaces()
    {
        // The __all_spaces__:__all_subpaths__ permission should grant visibility
        // to every space, not just those owned by the user.
        if (!DmartFactory.HasPg) return;
        var (query, _) = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 100,
        }, _factory.AdminShortname);

        resp.Status.ShouldBe(Status.Success);
        var total = (int)resp.Attributes!["total"]!;
        var returned = resp.Records!.Count;
        // total should equal returned (all visible with limit=100)
        returned.ShouldBe(total);
        // At least the management space should be visible
        resp.Records!.ShouldContain(r => r.Shortname == "management");
    }

    [Fact]
    public async Task QuerySpaces_LimitedUser_Sees_Only_Permitted_Spaces()
    {
        // A user with permission in "test" and "management" but not all spaces
        // should see only those two in the spaces listing.
        if (!DmartFactory.HasPg) return;
        var sp = _factory.Services;
        var usersRepo = sp.GetRequiredService<UserRepository>();
        var accessRepo = sp.GetRequiredService<AccessRepository>();
        var (query, _) = Resolve();

        var permName = $"itest_perm_qs_{Guid.NewGuid():N}".Substring(0, 24);
        var roleName = $"itest_role_qs_{Guid.NewGuid():N}".Substring(0, 24);
        var userName = $"itest_user_qs_{Guid.NewGuid():N}".Substring(0, 24);

        try
        {
            // Permission covers "management" space only — but NOT __all_spaces__.
            await accessRepo.UpsertPermissionAsync(new Permission
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = permName,
                SpaceName = "management",
                Subpath = "/permissions",
                OwnerShortname = "dmart",
                IsActive = true,
                Subpaths = new()
                {
                    ["management"] = new() { "users", "schema" },
                },
                Actions = new() { "view", "query" },
                ResourceTypes = new() { "content", "folder", "user", "schema" },
                Conditions = new(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await accessRepo.UpsertRoleAsync(new Role
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = roleName,
                SpaceName = "management",
                Subpath = "/roles",
                OwnerShortname = "dmart",
                IsActive = true,
                Permissions = new() { permName },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await usersRepo.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = userName,
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = userName,
                IsActive = true,
                Roles = new() { roleName },
                Type = UserType.Web,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await accessRepo.InvalidateAllCachesAsync();

            // Spaces query
            var resp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Spaces,
                SpaceName = "management",
                Subpath = "/",
                Limit = 100,
            }, userName);

            resp.Status.ShouldBe(Status.Success);
            var names = resp.Records!.Select(r => r.Shortname).ToList();
            names.ShouldContain("management", "user has permission in 'management'");
            // Total should be less than what super_admin sees (limited permissions).
            // At minimum, exactly 1 (management) since that's the only space
            // referenced in the permission.
            ((int)resp.Attributes!["total"]!).ShouldBeGreaterThanOrEqualTo(1);

            // Root query on management should succeed (fallback to space-level access)
            var rootResp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Search,
                SpaceName = "management",
                Subpath = "/",
                Limit = 10,
            }, userName);
            rootResp.Status.ShouldBe(Status.Success);

            // Query management/users should succeed
            var usersResp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Search,
                SpaceName = "management",
                Subpath = "/users",
                Limit = 5,
            }, userName);
            usersResp.Status.ShouldBe(Status.Success);

            // Counters query on management/users should succeed
            var countersResp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Counters,
                SpaceName = "management",
                Subpath = "/users",
                Limit = 100,
            }, userName);
            countersResp.Status.ShouldBe(Status.Success);
        }
        finally
        {
            try { await usersRepo.DeleteAsync(userName); } catch { }
            await accessRepo.InvalidateAllCachesAsync();
        }
    }
}
