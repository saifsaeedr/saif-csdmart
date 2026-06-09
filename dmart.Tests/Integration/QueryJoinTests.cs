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

// Regression tests for the client-side join feature on /managed/query.
// Mirrors Python dmart's _apply_client_joins behavior:
//   - JoinQuery.join_on is a comma-separated list of "left:right" pairs.
//   - Left values come from the base record; right values from the sub-query
//     result set. The synthesized sub-query search term "@<right>:<vals>"
//     pulls matching right records; they then land under each base record's
//     attributes["join"][<alias>] as a list.
public class QueryJoinTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public QueryJoinTests(DmartFactory factory) => _factory = factory;

    private (QueryService query, EntryRepository entries, SpaceRepository spaces) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<QueryService>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<SpaceRepository>());
    }

    [FactIfPg]
    public async Task Query_With_Join_Attaches_Matched_Records_Under_Alias()
    {
        var (query, entries, spaces) = Resolve();
        var spaceName = $"joint_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
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

            // Seed: two orders referencing two customers by shortname.
            await SeedEntryAsync(entries, spaceName, "/orders", "order_a", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["customer"] = JsonDocument.Parse("\"cust_one\"").RootElement,
                });
            await SeedEntryAsync(entries, spaceName, "/orders", "order_b", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["customer"] = JsonDocument.Parse("\"cust_two\"").RootElement,
                });
            // Right-side: two customer entries the join should match against.
            await SeedEntryAsync(entries, spaceName, "/customers", "cust_one", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["email"] = JsonDocument.Parse("\"one@example.com\"").RootElement,
                });
            await SeedEntryAsync(entries, spaceName, "/customers", "cust_two", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["email"] = JsonDocument.Parse("\"two@example.com\"").RootElement,
                });

            // Sub-query points at /customers; join_on matches the base record's
            // payload.body.customer against the right record's shortname.
            var subQueryJson = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["type"] = "subpath",
                ["space_name"] = spaceName,
                ["subpath"] = "customers",
                ["limit"] = 100,
                ["retrieve_json_payload"] = true,
            });

            var resp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Subpath,
                SpaceName = spaceName,
                Subpath = "orders",
                Limit = 100,
                RetrieveJsonPayload = true,
                Join = new()
                {
                    new JoinQuery
                    {
                        JoinOn = "payload.body.customer:shortname",
                        Alias = "customer",
                        Query = subQueryJson,
                    },
                },
            }, "dmart");

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();
            resp.Records!.Count.ShouldBe(2);

            // Every base record must carry attributes["join"]["customer"] with
            // one matched right record, whose shortname equals the base record's
            // payload.body.customer value.
            foreach (var rec in resp.Records)
            {
                rec.Attributes.ShouldNotBeNull();
                rec.Attributes!.ShouldContainKey("join");
                var joinDict = (Dictionary<string, object>)rec.Attributes["join"];
                joinDict.ShouldContainKey("customer");
                var matched = (List<Record>)joinDict["customer"];
                matched.Count.ShouldBe(1, $"order {rec.Shortname} should match exactly one customer");

                // Correlate by reaching into the seeded payload body to extract
                // the expected customer shortname.
                var payload = (Payload)rec.Attributes["payload"];
                var body = payload.Body!.Value;
                var expectedCustomer = body.GetProperty("customer").GetString();
                matched[0].Shortname.ShouldBe(expectedCustomer);
            }
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    // ── Join type coverage ────────────────────────────────────────────────
    // The four tests below share a fixture: two orders (one referencing a
    // customer that exists, one referencing a customer that does NOT), plus
    // three customers (two that no order references). That asymmetry lets a
    // single seed exercise every join semantic — matched base, unmatched
    // base, and unmatched right — and assert one cleanly per join type.

    [FactIfPg] public Task Join_Default_Type_Is_Left()    => RunJoinTypeScenario(joinType: null);
    [FactIfPg] public Task Join_Explicit_Left()           => RunJoinTypeScenario(joinType: "left");
    [FactIfPg] public Task Join_Inner_Drops_Unmatched_Base() => RunJoinTypeScenario(joinType: "inner");
    [FactIfPg] public Task Join_Right_Drops_Unmatched_Base_And_Appends_Unmatched_Right() => RunJoinTypeScenario(joinType: "right");
    [FactIfPg] public Task Join_Outer_Keeps_Unmatched_Base_And_Appends_Unmatched_Right() => RunJoinTypeScenario(joinType: "outer");

    private async Task RunJoinTypeScenario(string? joinType)
    {
        var (query, entries, spaces) = Resolve();
        var spaceName = $"jt_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
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

            // Orders: matched_order → customer_a (exists); unmatched_order
            // → ghost_customer (no such customer record).
            await SeedEntryAsync(entries, spaceName, "/orders", "matched_order", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["customer"] = JsonDocument.Parse("\"customer_a\"").RootElement,
                });
            await SeedEntryAsync(entries, spaceName, "/orders", "unmatched_order", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["customer"] = JsonDocument.Parse("\"ghost_customer\"").RootElement,
                });

            // Customers: customer_a is referenced; customer_b + customer_c
            // are unreferenced (they'll only surface under right/outer).
            await SeedEntryAsync(entries, spaceName, "/customers", "customer_a", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["email"] = JsonDocument.Parse("\"a@example.com\"").RootElement,
                });
            await SeedEntryAsync(entries, spaceName, "/customers", "customer_b", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["email"] = JsonDocument.Parse("\"b@example.com\"").RootElement,
                });
            await SeedEntryAsync(entries, spaceName, "/customers", "customer_c", ResourceType.Content,
                new Dictionary<string, JsonElement>
                {
                    ["email"] = JsonDocument.Parse("\"c@example.com\"").RootElement,
                });

            var subQueryDict = new Dictionary<string, object>
            {
                ["type"] = "subpath",
                ["space_name"] = spaceName,
                ["subpath"] = "customers",
                ["limit"] = 100,
                ["retrieve_json_payload"] = true,
            };
            var subQueryJson = JsonSerializer.SerializeToElement(subQueryDict);

            var joinPayload = new Dictionary<string, object>
            {
                ["join_on"] = "payload.body.customer:shortname",
                ["alias"] = "customer",
                ["query"] = subQueryJson,
            };
            if (joinType is not null) joinPayload["type"] = joinType;

            // Serialize through Query/JoinQuery JSON to exercise the same
            // deserialization path the HTTP layer uses (catches a missing
            // [JsonSerializable] registration for JoinType).
            var queryDict = new Dictionary<string, object>
            {
                ["type"] = "subpath",
                ["space_name"] = spaceName,
                ["subpath"] = "orders",
                ["limit"] = 100,
                ["retrieve_json_payload"] = true,
                ["join"] = new[] { joinPayload },
            };
            var wireJson = JsonSerializer.Serialize(queryDict);
            var deserialized = JsonSerializer.Deserialize(wireJson, Dmart.Models.Json.DmartJsonContext.Default.Query);
            deserialized.ShouldNotBeNull();

            var resp = await query.ExecuteAsync(deserialized!, "dmart");

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();

            // Per-type expectations. The keying scheme below uses
            // "<subpath>/<shortname>" so we can assert presence without
            // caring about ordering.
            var shortnames = resp.Records!.Select(r => $"{r.Subpath}/{r.Shortname}").ToHashSet();

            switch (joinType)
            {
                case null:
                case "left":
                {
                    // Left (default): both orders present; matched_order has
                    // customer_a under alias, unmatched_order has empty list.
                    shortnames.ShouldContain("orders/matched_order");
                    shortnames.ShouldContain("orders/unmatched_order");
                    shortnames.Count.ShouldBe(2);
                    AssertMatchCount(resp.Records, "matched_order", expected: 1);
                    AssertMatchCount(resp.Records, "unmatched_order", expected: 0);
                    break;
                }
                case "inner":
                {
                    // Inner: unmatched_order is filtered out.
                    shortnames.ShouldContain("orders/matched_order");
                    shortnames.ShouldNotContain("orders/unmatched_order");
                    shortnames.Count.ShouldBe(1);
                    AssertMatchCount(resp.Records, "matched_order", expected: 1);
                    break;
                }
                case "right":
                {
                    // Right: matched_order kept, unmatched_order dropped,
                    // and customer_b / customer_c (never referenced)
                    // appended. customer_a should NOT appear standalone —
                    // it's already under matched_order's alias.
                    shortnames.ShouldContain("orders/matched_order");
                    shortnames.ShouldNotContain("orders/unmatched_order");
                    shortnames.ShouldContain("customers/customer_b");
                    shortnames.ShouldContain("customers/customer_c");
                    shortnames.ShouldNotContain("customers/customer_a");
                    shortnames.Count.ShouldBe(3);
                    AssertMatchCount(resp.Records, "matched_order", expected: 1);
                    break;
                }
                case "outer":
                {
                    // Outer: both orders kept + appended unmatched rights.
                    shortnames.ShouldContain("orders/matched_order");
                    shortnames.ShouldContain("orders/unmatched_order");
                    shortnames.ShouldContain("customers/customer_b");
                    shortnames.ShouldContain("customers/customer_c");
                    shortnames.ShouldNotContain("customers/customer_a");
                    shortnames.Count.ShouldBe(4);
                    AssertMatchCount(resp.Records, "matched_order", expected: 1);
                    AssertMatchCount(resp.Records, "unmatched_order", expected: 0);
                    break;
                }
            }
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    // ── Narrowing optimization fallback ───────────────────────────────────
    // When a base-record value contains a parser metachar the narrowing
    // path can't safely synthesize `@right:v1|v2` and bails to fetching the
    // right side with just the user's search, matching client-side. The
    // fallback must still produce correct join output — if a regression
    // breaks it, the visible symptom is the unsafe base record matching
    // nothing instead of nothing-or-real-match.
    //
    // Parameterized across representative metachars so a future tightening
    // (or loosening) of the parser's grammar shows up here rather than
    // silently changing whether the narrowing optimization fires.
    [TheoryIfPg]
    [InlineData("|")]
    [InlineData("*")]
    [InlineData(":")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("@")]
    [InlineData("\\")]
    [InlineData(" ")]   // whitespace terminates a value token
    public async Task Join_With_Unsafe_Base_Value_Falls_Back_To_Client_Side_Match(string metachar)
    {
        var (query, entries, spaces) = Resolve();
        var spaceName = $"jt_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
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

            await SeedEntryAsync(entries, spaceName, "/orders", "safe_order", ResourceType.Content,
                new() { ["customer"] = JsonDocument.Parse("\"customer_a\"").RootElement });
            // The metachar in the value makes
            // SearchExpressionParser.IsSafeForAlternationValue return false,
            // forcing the client-side join fallback. Without it the join
            // would throw or silently un-join. Quoting the JSON literal
            // escapes backslash so `\\` lands as a single `\` in the value.
            var unsafeValue = $"cust{metachar}escape";
            var unsafeJsonValue = JsonSerializer.Serialize(unsafeValue);
            await SeedEntryAsync(entries, spaceName, "/orders", "unsafe_order", ResourceType.Content,
                new() { ["customer"] = JsonDocument.Parse(unsafeJsonValue).RootElement });
            await SeedEntryAsync(entries, spaceName, "/customers", "customer_a", ResourceType.Content,
                new() { ["email"] = JsonDocument.Parse("\"a@example.com\"").RootElement });

            var subQueryJson = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["type"] = "subpath",
                ["space_name"] = spaceName,
                ["subpath"] = "customers",
                ["limit"] = 100,
                ["retrieve_json_payload"] = true,
            });

            var resp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Subpath,
                SpaceName = spaceName,
                Subpath = "orders",
                Limit = 100,
                RetrieveJsonPayload = true,
                Join = new()
                {
                    new JoinQuery
                    {
                        JoinOn = "payload.body.customer:shortname",
                        Alias = "customer",
                        Query = subQueryJson,
                    },
                },
            }, "dmart");

            resp.Status.ShouldBe(Status.Success,
                customMessage: $"join with unsafe metachar '{metachar}' should not error: {resp.Error?.Message}");
            resp.Records.ShouldNotBeNull();
            resp.Records!.Count.ShouldBe(2);
            AssertMatchCount(resp.Records, "safe_order", expected: 1);
            AssertMatchCount(resp.Records, "unsafe_order", expected: 0);
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    // ── Right/Outer boundary cases ────────────────────────────────────────
    // Right with zero matched base must still surface every right record —
    // the join's whole purpose for that input shape is "what rights does
    // nobody reference?" If the appended-rights path is gated on having any
    // matched base, this is the test that catches it.
    [FactIfPg]
    public async Task Right_Join_With_No_Matched_Base_Returns_Only_Unmatched_Rights()
    {
        var (query, entries, spaces) = Resolve();
        var spaceName = $"jt_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
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

            await SeedEntryAsync(entries, spaceName, "/orders", "ghost_order", ResourceType.Content,
                new() { ["customer"] = JsonDocument.Parse("\"nonexistent_customer\"").RootElement });
            await SeedEntryAsync(entries, spaceName, "/customers", "customer_x", ResourceType.Content,
                new() { ["email"] = JsonDocument.Parse("\"x@example.com\"").RootElement });
            await SeedEntryAsync(entries, spaceName, "/customers", "customer_y", ResourceType.Content,
                new() { ["email"] = JsonDocument.Parse("\"y@example.com\"").RootElement });

            var resp = await ExecuteJoinViaWire(query, spaceName, "right");

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();
            var keys = resp.Records!.Select(r => $"{r.Subpath}/{r.Shortname}").ToHashSet();
            keys.ShouldContain("customers/customer_x");
            keys.ShouldContain("customers/customer_y");
            keys.ShouldNotContain("orders/ghost_order");
            keys.Count.ShouldBe(2);
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    // Outer with an empty right side: nothing to append, but every base
    // record must still survive with an empty matched-list under the alias.
    // Asserts the "kept-base" branch keeps mutating attributes["join"] even
    // when the right-side fetch returned zero rows.
    [FactIfPg]
    public async Task Outer_Join_With_Empty_Right_Keeps_All_Base_With_Empty_Matches()
    {
        var (query, entries, spaces) = Resolve();
        var spaceName = $"jt_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
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

            // Orders only — no customers seeded, so the sub-query returns
            // an empty right set.
            await SeedEntryAsync(entries, spaceName, "/orders", "order_a", ResourceType.Content,
                new() { ["customer"] = JsonDocument.Parse("\"customer_a\"").RootElement });
            await SeedEntryAsync(entries, spaceName, "/orders", "order_b", ResourceType.Content,
                new() { ["customer"] = JsonDocument.Parse("\"customer_b\"").RootElement });

            var resp = await ExecuteJoinViaWire(query, spaceName, "outer");

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();
            var keys = resp.Records!.Select(r => $"{r.Subpath}/{r.Shortname}").ToHashSet();
            keys.ShouldContain("orders/order_a");
            keys.ShouldContain("orders/order_b");
            keys.Count.ShouldBe(2);
            AssertMatchCount(resp.Records, "order_a", expected: 0);
            AssertMatchCount(resp.Records, "order_b", expected: 0);
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    // ── Pagination ordering: filter-before-paginate ───────────────────────
    // Regression for the "paginate-before-join" bug. With limit < matched
    // count, an INNER join must build the page from the JOINED set, not from
    // the base page. Seed 10 orders (even shortname → real customer, odd →
    // ghost) so exactly 5 match; the customer side has one record. An inner
    // join with limit 3 must return 3 matched rows with total 5 — not the
    // 2 rows / total 10 the pre-fix paginate-then-join produced (base page
    // order_00..order_02, then the join deletes the odd ghost from inside
    // the page).
    [FactIfPg]
    public async Task Inner_Join_Filters_Before_Paginating()
    {
        var (query, entries, spaces) = Resolve();
        var spaceName = $"jp_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            await SeedPaginationFixtureAsync(entries, spaces, spaceName);
            var matched = new[] { "order_00", "order_02", "order_04", "order_06", "order_08" };

            var page0 = await ExecutePagedJoin(query, spaceName, JoinType.Inner, limit: 3, offset: 0);
            page0.Status.ShouldBe(Status.Success, customMessage: page0.Error?.Message);
            page0.Records.ShouldNotBeNull();
            page0.Records!.Count.ShouldBe(3, "inner-join page must be filled from the joined set, not the base page");
            ((int)page0.Attributes!["total"]!).ShouldBe(5, "total must reflect the post-join cardinality");
            foreach (var r in page0.Records)
                matched.ShouldContain(r.Shortname, $"unexpected unmatched record {r.Shortname} on the page");

            var page1 = await ExecutePagedJoin(query, spaceName, JoinType.Inner, limit: 3, offset: 3);
            page1.Records.ShouldNotBeNull();
            page1.Records!.Count.ShouldBe(2);
            ((int)page1.Attributes!["total"]!).ShouldBe(5);

            // Across both pages: every matched order appears exactly once.
            var seen = page0.Records.Select(r => r.Shortname)
                .Concat(page1.Records.Select(r => r.Shortname)).ToList();
            seen.Count.ShouldBe(5);
            seen.ToHashSet().Count.ShouldBe(5, "pages must not overlap");
            foreach (var sn in matched) seen.ShouldContain(sn);
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    // LEFT join keeps every base record, so its cardinality is unchanged and
    // pagination still pages the base table directly: limit 3 → 3 records,
    // total 10. Guards against the fix accidentally re-paginating the cheap
    // left path.
    [FactIfPg]
    public async Task Left_Join_Paginates_The_Base_Table_Unchanged()
    {
        var (query, entries, spaces) = Resolve();
        var spaceName = $"jp_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            await SeedPaginationFixtureAsync(entries, spaces, spaceName);

            var resp = await ExecutePagedJoin(query, spaceName, JoinType.Left, limit: 3, offset: 0);
            resp.Status.ShouldBe(Status.Success, customMessage: resp.Error?.Message);
            resp.Records.ShouldNotBeNull();
            resp.Records!.Count.ShouldBe(3);
            ((int)resp.Attributes!["total"]!).ShouldBe(10, "left join keeps base cardinality → total is the base count");
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    // Seeds 10 orders order_00..order_09 (even → customer_a, odd → a ghost
    // with no matching customer) plus the single customer_a they point at.
    private static async Task SeedPaginationFixtureAsync(
        EntryRepository entries, SpaceRepository spaces, string spaceName)
    {
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

        for (var i = 0; i < 10; i++)
        {
            var customer = i % 2 == 0 ? "customer_a" : $"ghost_{i}";
            await SeedEntryAsync(entries, spaceName, "/orders", $"order_{i:D2}", ResourceType.Content,
                new() { ["customer"] = JsonDocument.Parse($"\"{customer}\"").RootElement });
        }
        await SeedEntryAsync(entries, spaceName, "/customers", "customer_a", ResourceType.Content,
            new() { ["email"] = JsonDocument.Parse("\"a@example.com\"").RootElement });
    }

    // Inner/left join over /orders→/customers with explicit paging + a stable
    // shortname sort so the page boundaries are deterministic.
    private static async Task<Response> ExecutePagedJoin(
        QueryService query, string spaceName, JoinType joinType, int limit, int offset)
    {
        var subQueryJson = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "subpath",
            ["space_name"] = spaceName,
            ["subpath"] = "customers",
            ["limit"] = 100,
            ["retrieve_json_payload"] = true,
        });

        return await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = spaceName,
            Subpath = "orders",
            Limit = limit,
            Offset = offset,
            SortBy = "shortname",
            SortType = SortType.Ascending,
            RetrieveJsonPayload = true,
            Join = new()
            {
                new JoinQuery
                {
                    JoinOn = "payload.body.customer:shortname",
                    Alias = "customer",
                    Query = subQueryJson,
                    Type = joinType,
                },
            },
        }, "dmart");
    }

    // ── Complex case: paging across a RIGHT join's heterogeneous result ────
    // The hardest pagination shape for the fix. A RIGHT join yields a result
    // that is two concatenated segments: matched base survivors FIRST (the
    // unmatched base records dropped), then the right records nobody
    // referenced APPENDED. A correct "join-then-paginate" implementation must:
    //   1. run the join over the FULL base set, so all 3 survivors are found
    //      even though the page limit (2) is smaller than the survivor count;
    //   2. report `total` as the whole combined set (survivors + appended),
    //      identical on every page;
    //   3. walk pages cleanly across the survivor→appended boundary — one
    //      page straddles the last base survivor and the first appended right;
    //   4. never surface dropped base rows or already-matched (referenced)
    //      rights as standalone records.
    //
    // Fixture: 6 orders (even shortname → an existing customer a/b/c, odd →
    // a ghost) so exactly 3 survive; 6 customers (a/b/c referenced, d/e/f
    // not) so exactly 3 are appended. With a stable shortname sort on both
    // sides the joined set is deterministic:
    //   [order_00, order_02, order_04, customer_d, customer_e, customer_f]
    // → total 6, walked here as three pages of 2.
    [FactIfPg]
    public async Task Right_Join_Paginates_Across_The_Survivor_To_Appended_Boundary()
    {
        var (query, entries, spaces) = Resolve();
        var spaceName = $"jb_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
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

            // Even orders reference customers that exist; odd reference ghosts.
            var orderCustomer = new[] { "customer_a", "ghost_1", "customer_b", "ghost_3", "customer_c", "ghost_5" };
            for (var i = 0; i < 6; i++)
                await SeedEntryAsync(entries, spaceName, "/orders", $"order_{i:D2}", ResourceType.Content,
                    new() { ["customer"] = JsonDocument.Parse($"\"{orderCustomer[i]}\"").RootElement });

            // a/b/c are referenced by orders; d/e/f are never referenced and
            // are what a right join must append.
            foreach (var c in new[] { "customer_a", "customer_b", "customer_c", "customer_d", "customer_e", "customer_f" })
                await SeedEntryAsync(entries, spaceName, "/customers", c, ResourceType.Content,
                    new() { ["email"] = JsonDocument.Parse($"\"{c}@example.com\"").RootElement });

            var expectedPages = new[]
            {
                new[] { "orders/order_00", "orders/order_02" },
                new[] { "orders/order_04", "customers/customer_d" },   // ← straddles the boundary
                new[] { "customers/customer_e", "customers/customer_f" },
            };

            var collected = new List<string>();
            for (var p = 0; p < expectedPages.Length; p++)
            {
                var resp = await ExecutePagedRightJoin(query, spaceName, limit: 2, offset: p * 2);
                resp.Status.ShouldBe(Status.Success, customMessage: resp.Error?.Message);
                resp.Records.ShouldNotBeNull();
                ((int)resp.Attributes!["total"]!).ShouldBe(6, $"total must be the full joined-set size on page {p}");
                ((int)resp.Attributes!["returned"]!).ShouldBe(2, $"page {p} returned mismatch");

                var keys = resp.Records!.Select(r => $"{r.Subpath}/{r.Shortname}").ToList();
                keys.ShouldBe(expectedPages[p]);   // order-sensitive
                collected.AddRange(keys);
            }

            // One page past the end: empty, but the total is still the full set.
            var tail = await ExecutePagedRightJoin(query, spaceName, limit: 2, offset: 6);
            tail.Records.ShouldNotBeNull();
            tail.Records!.Count.ShouldBe(0);
            ((int)tail.Attributes!["total"]!).ShouldBe(6);
            ((int)tail.Attributes!["returned"]!).ShouldBe(0);

            // Full coverage, no overlap; dropped base and referenced rights
            // never appear standalone.
            collected.Count.ShouldBe(6);
            collected.ToHashSet().Count.ShouldBe(6, "pages overlapped");
            foreach (var dropped in new[] { "orders/order_01", "orders/order_03", "orders/order_05" })
                collected.ShouldNotContain(dropped);
            foreach (var referenced in new[] { "customers/customer_a", "customers/customer_b", "customers/customer_c" })
                collected.ShouldNotContain(referenced);

            // Inspect the straddling page: the base survivor carries its
            // matched customer; the appended right carries an empty match list
            // plus the right-origin discriminator.
            var boundary = await ExecutePagedRightJoin(query, spaceName, limit: 2, offset: 2);
            AssertMatchCount(boundary.Records!, "order_04", expected: 1);

            var appended = boundary.Records!.First(r => r.Shortname == "customer_d");
            var appendedJoin = (Dictionary<string, object>)appended.Attributes!["join"];
            ((List<Record>)appendedJoin["customer"]).Count.ShouldBe(0, "appended right must carry an empty match list");
            appendedJoin.ShouldContainKey("_join_origin");
            appendedJoin["_join_origin"].ShouldBe("right");
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    // Right join over /orders→/customers with explicit paging. Both the base
    // and the sub-query carry a stable shortname sort so the survivor segment
    // and the appended-rights segment are each deterministically ordered.
    private static async Task<Response> ExecutePagedRightJoin(
        QueryService query, string spaceName, int limit, int offset)
    {
        var subQuery = new Query
        {
            Type = QueryType.Subpath,
            SpaceName = spaceName,
            Subpath = "customers",
            Limit = 100,
            SortBy = "shortname",
            SortType = SortType.Ascending,
            RetrieveJsonPayload = true,
        };
        var subQueryJson = JsonSerializer.SerializeToElement(subQuery, Dmart.Models.Json.DmartJsonContext.Default.Query);

        return await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = spaceName,
            Subpath = "orders",
            Limit = limit,
            Offset = offset,
            SortBy = "shortname",
            SortType = SortType.Ascending,
            RetrieveJsonPayload = true,
            Join = new()
            {
                new JoinQuery
                {
                    JoinOn = "payload.body.customer:shortname",
                    Alias = "customer",
                    Query = subQueryJson,
                    Type = JoinType.Right,
                },
            },
        }, "dmart");
    }

    // Round-trip a join request through the same JSON deserializer the HTTP
    // layer uses, so the JoinType wire-string also gets exercised.
    private static async Task<Response> ExecuteJoinViaWire(QueryService query, string spaceName, string joinType)
    {
        var subQueryJson = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "subpath",
            ["space_name"] = spaceName,
            ["subpath"] = "customers",
            ["limit"] = 100,
            ["retrieve_json_payload"] = true,
        });
        var queryDict = new Dictionary<string, object>
        {
            ["type"] = "subpath",
            ["space_name"] = spaceName,
            ["subpath"] = "orders",
            ["limit"] = 100,
            ["retrieve_json_payload"] = true,
            ["join"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["join_on"] = "payload.body.customer:shortname",
                    ["alias"] = "customer",
                    ["query"] = subQueryJson,
                    ["type"] = joinType,
                },
            },
        };
        var wireJson = JsonSerializer.Serialize(queryDict);
        var deserialized = JsonSerializer.Deserialize(wireJson, Dmart.Models.Json.DmartJsonContext.Default.Query)!;
        return await query.ExecuteAsync(deserialized, "dmart");
    }

    private static void AssertMatchCount(List<Record> records, string shortname, int expected)
    {
        var rec = records.FirstOrDefault(r => r.Shortname == shortname);
        rec.ShouldNotBeNull($"expected record {shortname} in response");
        rec!.Attributes.ShouldNotBeNull();
        rec.Attributes!.ShouldContainKey("join");
        var joinDict = (Dictionary<string, object>)rec.Attributes["join"];
        joinDict.ShouldContainKey("customer");
        var matched = (List<Record>)joinDict["customer"];
        matched.Count.ShouldBe(expected, $"{shortname} should have {expected} matches under alias");
    }

    private static async Task SeedEntryAsync(EntryRepository entries, string spaceName,
        string subpath, string shortname, ResourceType rt,
        Dictionary<string, JsonElement> payloadBody)
    {
        // Payload.Body is a JsonElement — build one from the provided fields
        // and hand it through so the join can read payload.body.<field>.
        var jsonDoc = JsonDocument.Parse(JsonSerializer.Serialize(payloadBody));
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = spaceName,
            Subpath = subpath,
            ResourceType = rt,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = jsonDoc.RootElement.Clone(),
            },
        });
    }
}
