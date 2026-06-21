using System.Collections.Generic;
using System.Linq;
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

// End-to-end coverage for the `or` boolean keyword (added 2026-06-20).
// SearchExpressionParserTests pins the emitted SQL shape; these confirm the
// SQL actually filters as specified against a real PostgreSQL instance,
// through QueryService.ExecuteAsync — including AND-binds-tighter-than-OR
// precedence and paren grouping overriding it.
public class QuerySearchOrKeywordTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public QuerySearchOrKeywordTests(DmartFactory factory) => _factory = factory;

    private (QueryService query, EntryRepository entries, SpaceRepository spaces) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<QueryService>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<SpaceRepository>());
    }

    // Four records spanning the color × size grid so OR / AND / precedence
    // each have a discriminating row:
    //   red_small   {color:red,   size:small}
    //   red_large   {color:red,   size:large}
    //   blue_small  {color:blue,  size:small}
    //   green_large {color:green, size:large}
    private async Task<string> SeedFixture(EntryRepository entries, SpaceRepository spaces)
    {
        var spaceName = $"sp_{Guid.NewGuid():N}".Substring(0, 12);
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await Seed(entries, spaceName, "red_small", "{\"color\":\"red\",\"size\":\"small\"}");
        await Seed(entries, spaceName, "red_large", "{\"color\":\"red\",\"size\":\"large\"}");
        await Seed(entries, spaceName, "blue_small", "{\"color\":\"blue\",\"size\":\"small\"}");
        await Seed(entries, spaceName, "green_large", "{\"color\":\"green\",\"size\":\"large\"}");
        return spaceName;
    }

    private static async Task Seed(EntryRepository entries, string spaceName,
        string shortname, string bodyJson)
    {
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = spaceName,
            Subpath = "/items",
            ResourceType = ResourceType.Content,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(bodyJson).RootElement.Clone(),
            },
        });
    }

    private static async Task<HashSet<string>> RunSearch(QueryService query, string spaceName, string search)
    {
        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = spaceName,
            Subpath = "items",
            Limit = 100,
            RetrieveJsonPayload = true,
            Search = search,
        }, "dmart");
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();
        return resp.Records!.Select(r => r.Shortname).ToHashSet();
    }

    [FactIfPg]
    public async Task Or_Keyword_Returns_Union_Of_Both_Branches()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // color=red OR color=blue
            var hits = await RunSearch(query, sn,
                "@payload.body.color:red or @payload.body.color:blue");
            hits.ShouldContain("red_small");
            hits.ShouldContain("red_large");
            hits.ShouldContain("blue_small");
            hits.ShouldNotContain("green_large");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task And_Binds_Tighter_Than_Or_End_To_End()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // (color=red AND size=large) OR color=blue
            var hits = await RunSearch(query, sn,
                "@payload.body.color:red @payload.body.size:large or @payload.body.color:blue");
            hits.ShouldContain("red_large");  // satisfies the AND group
            hits.ShouldContain("blue_small"); // satisfies the bare OR branch
            hits.ShouldNotContain("red_small");   // red but small → fails AND, not blue
            hits.ShouldNotContain("green_large"); // neither branch
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task Parens_Override_Precedence_End_To_End()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // (color=red OR color=blue) AND size=small
            var hits = await RunSearch(query, sn,
                "(@payload.body.color:red or @payload.body.color:blue) @payload.body.size:small");
            hits.ShouldContain("red_small");
            hits.ShouldContain("blue_small");
            hits.ShouldNotContain("red_large");   // red but large
            hits.ShouldNotContain("green_large"); // neither color, and large
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task Whitespace_Between_Paren_Groups_Is_AND_Not_OR()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // BREAKING CHANGE guard: `(A) (B)` is AND, so this returns only the
            // single row that is BOTH red AND small — not the old OR union.
            var hits = await RunSearch(query, sn,
                "(@payload.body.color:red) (@payload.body.size:small)");
            hits.ShouldContain("red_small");
            hits.ShouldNotContain("red_large");
            hits.ShouldNotContain("blue_small");
            hits.ShouldNotContain("green_large");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task Stray_Close_Paren_Before_Or_Still_Means_Or()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // Regression: a stray ')' before `or` must not silently become AND.
            // color=red OR color=blue, despite the unbalanced ')'.
            var hits = await RunSearch(query, sn,
                "@payload.body.color:red) or @payload.body.color:blue");
            hits.ShouldContain("red_small");
            hits.ShouldContain("red_large");
            hits.ShouldContain("blue_small");
            hits.ShouldNotContain("green_large");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }
}
