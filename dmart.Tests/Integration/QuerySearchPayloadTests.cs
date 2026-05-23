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

// End-to-end coverage for the two new payload-search affordances:
//   - `@payload.body.<path>:null`     → match rows where the path is
//     JSON-null OR missing.
//   - `@payload.body.<path>:*foo*`    → ILIKE-style wildcard (prefix,
//     suffix, contains) on the string at the path.
//
// SearchExpressionParserTests covers the emitted SQL shape; these tests
// confirm the SQL actually filters the way we expect against a real
// PostgreSQL instance, end-to-end through QueryService.ExecuteAsync.
public class QuerySearchPayloadTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public QuerySearchPayloadTests(DmartFactory factory) => _factory = factory;

    private (QueryService query, EntryRepository entries, SpaceRepository spaces) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<QueryService>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<SpaceRepository>());
    }

    // ── Fixture seeding ───────────────────────────────────────────────────
    // Four records, varying only in payload.body.x:
    //   has_value      → "delate it now"        (string contains "delate")
    //   has_prefix     → "delate"                (string starts with "delate")
    //   has_suffix     → "please delate"        (string ends with "delate")
    //   has_null       → JSON null              (explicit null)
    //   has_missing    → key absent             (no `x` in body at all)
    //   has_other      → "hello world"          (string, no match)
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

        await Seed(entries, spaceName, "has_value", "{\"x\":\"delate it now\"}");
        await Seed(entries, spaceName, "has_prefix", "{\"x\":\"delate\"}");
        await Seed(entries, spaceName, "has_suffix", "{\"x\":\"please delate\"}");
        await Seed(entries, spaceName, "has_null", "{\"x\":null}");
        await Seed(entries, spaceName, "has_missing", "{\"y\":\"unrelated\"}");
        await Seed(entries, spaceName, "has_other", "{\"x\":\"hello world\"}");
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

    // ── Null search ───────────────────────────────────────────────────────

    [FactIfPg]
    public async Task Search_Null_Matches_JsonNull_And_Missing_Key()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            var hits = await RunSearch(query, sn, "@payload.body.x:null");
            hits.ShouldContain("has_null");
            hits.ShouldContain("has_missing");
            hits.ShouldNotContain("has_value");
            hits.ShouldNotContain("has_prefix");
            hits.ShouldNotContain("has_suffix");
            hits.ShouldNotContain("has_other");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task Search_Null_Negated_Matches_Only_Existing_NonNull()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            var hits = await RunSearch(query, sn, "-@payload.body.x:null");
            hits.ShouldContain("has_value");
            hits.ShouldContain("has_prefix");
            hits.ShouldContain("has_suffix");
            hits.ShouldContain("has_other");
            hits.ShouldNotContain("has_null");
            hits.ShouldNotContain("has_missing");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    // ── Wildcard search ───────────────────────────────────────────────────

    [FactIfPg]
    public async Task Search_Wildcard_Contains_Matches_Substring_Anywhere()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // `*delate*` matches a substring at any position — should pick
            // up has_value (middle), has_prefix (start), has_suffix (end).
            var hits = await RunSearch(query, sn, "@payload.body.x:*delate*");
            hits.ShouldContain("has_value");
            hits.ShouldContain("has_prefix");
            hits.ShouldContain("has_suffix");
            hits.ShouldNotContain("has_null");
            hits.ShouldNotContain("has_missing");
            hits.ShouldNotContain("has_other");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task Search_Wildcard_Prefix_Matches_Only_Strings_That_Start()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // `delate*` requires "delate" at the start. has_prefix starts
            // with it, has_value also starts with it ("delate it now") —
            // both should match. has_suffix ("please delate") must NOT.
            var hits = await RunSearch(query, sn, "@payload.body.x:delate*");
            hits.ShouldContain("has_prefix");
            hits.ShouldContain("has_value");
            hits.ShouldNotContain("has_suffix");
            hits.ShouldNotContain("has_other");
            hits.ShouldNotContain("has_null");
            hits.ShouldNotContain("has_missing");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task Search_Wildcard_Suffix_Matches_Only_Strings_That_End()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // `*delate` requires "delate" at the end. has_prefix is just
            // "delate" (ends with itself), has_suffix ends with "delate"
            // — both match. has_value ends with "now" — must not.
            var hits = await RunSearch(query, sn, "@payload.body.x:*delate");
            hits.ShouldContain("has_prefix");
            hits.ShouldContain("has_suffix");
            hits.ShouldNotContain("has_value");
            hits.ShouldNotContain("has_other");
            hits.ShouldNotContain("has_null");
            hits.ShouldNotContain("has_missing");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task Search_Wildcard_Negated_Excludes_Matches_Includes_Everything_Else()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // -@x:*delate* excludes the three "delate" matches. Importantly
            // it keeps rows where the field is missing or null — those
            // are "not a matching string", so the negated wildcard accepts
            // them.
            var hits = await RunSearch(query, sn, "-@payload.body.x:*delate*");
            hits.ShouldContain("has_other");
            hits.ShouldContain("has_null");
            hits.ShouldContain("has_missing");
            hits.ShouldNotContain("has_value");
            hits.ShouldNotContain("has_prefix");
            hits.ShouldNotContain("has_suffix");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    [FactIfPg]
    public async Task Search_Exact_Match_Still_Works_With_NonWildcard_Value()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            // Sanity guard: a plain value without `*` should still use
            // JSONB containment and only return the exact match.
            var hits = await RunSearch(query, sn, "@payload.body.x:delate");
            hits.ShouldContain("has_prefix");
            hits.ShouldNotContain("has_value");
            hits.ShouldNotContain("has_suffix");
            hits.ShouldNotContain("has_other");
            hits.ShouldNotContain("has_null");
            hits.ShouldNotContain("has_missing");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }
}
