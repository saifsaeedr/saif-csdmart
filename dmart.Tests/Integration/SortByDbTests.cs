using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end validation of the JSON-path + comma-list sort_by support.
// Unit tests (QueryHelperTests) pin the emitted SQL shape; these tests run
// the query against the live PG to prove the SQL is accepted and the row
// ordering comes back as expected. Seeds a fresh space with throwaway
// entries so no existing fixtures are disturbed.
public sealed class SortByDbTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SortByDbTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task JsonPath_Sort_Numeric_Orders_Values_Numerically()
    {
        if (!DmartFactory.HasPg) return;
        _factory.CreateClient();
        var entryRepo = _factory.Services.GetRequiredService<EntryRepository>();
        var query = _factory.Services.GetRequiredService<QueryService>();

        var space = $"sortby_{Guid.NewGuid():N}"[..16];
        var subpath = "/items";
        // 2, 10, 1 — picked so alphabetic sort would put 10 before 2; numeric
        // sort puts them 1, 2, 10. If the CASE-numeric branch in the emitted
        // SQL is wrong we'd see the alphabetic order and the test would fail.
        var ranks = new[] { ("a", 10), ("b", 2), ("c", 1) };

        try
        {
            foreach (var (shortname, rank) in ranks)
            {
                var payloadBody = JsonDocument.Parse($"{{\"rank\":{rank}}}").RootElement.Clone();
                await entryRepo.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = shortname,
                    SpaceName = space,
                    Subpath = subpath,
                    OwnerShortname = "dmart",
                    ResourceType = ResourceType.Content,
                    IsActive = true,
                    Payload = new Payload { ContentType = ContentType.Json, Body = payloadBody },
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            var resp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = subpath,
                FilterSchemaNames = new(),
                SortBy = "payload.body.rank",
                SortType = SortType.Ascending,
                Limit = 10,
                RetrieveJsonPayload = true,
            }, _factory.AdminShortname);

            resp.Status.ShouldBe(Status.Success);
            resp.Records!.Count.ShouldBe(3);
            // 1 → 2 → 10 (numeric), NOT 1 → 10 → 2 (alphabetic)
            resp.Records.Select(r => r.Shortname).ToArray()
                .ShouldBe(new[] { "c", "b", "a" });
        }
        finally
        {
            foreach (var (shortname, _) in ranks)
            {
                try
                {
                    await entryRepo.DeleteAsync(space, subpath, shortname, ResourceType.Content);
                }
                catch { }
            }
        }
    }

    [Fact]
    public async Task CommaList_Sort_Path_Then_Column_Applies_Tiebreaker()
    {
        if (!DmartFactory.HasPg) return;
        _factory.CreateClient();
        var entryRepo = _factory.Services.GetRequiredService<EntryRepository>();
        var query = _factory.Services.GetRequiredService<QueryService>();

        var space = $"sortcomma_{Guid.NewGuid():N}"[..16];
        var subpath = "/items";
        // Two rows share rank=5. With the tiebreaker "shortname ASC", the
        // secondary order picks z_same AFTER a_same.
        var rows = new[] { ("a_same", 5), ("z_same", 5), ("mid", 3) };

        try
        {
            foreach (var (shortname, rank) in rows)
            {
                var payloadBody = JsonDocument.Parse($"{{\"rank\":{rank}}}").RootElement.Clone();
                await entryRepo.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = shortname,
                    SpaceName = space,
                    Subpath = subpath,
                    OwnerShortname = "dmart",
                    ResourceType = ResourceType.Content,
                    IsActive = true,
                    Payload = new Payload { ContentType = ContentType.Json, Body = payloadBody },
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            var resp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = subpath,
                FilterSchemaNames = new(),
                SortBy = "payload.body.rank, shortname",
                SortType = SortType.Ascending,
                Limit = 10,
                RetrieveJsonPayload = true,
            }, _factory.AdminShortname);

            resp.Status.ShouldBe(Status.Success);
            resp.Records!.Count.ShouldBe(3);
            // rank 3 first, then rank 5 tied — a_same before z_same.
            resp.Records.Select(r => r.Shortname).ToArray()
                .ShouldBe(new[] { "mid", "a_same", "z_same" });
        }
        finally
        {
            foreach (var (shortname, _) in rows)
            {
                try
                {
                    await entryRepo.DeleteAsync(space, subpath, shortname, ResourceType.Content);
                }
                catch { }
            }
        }
    }
}
