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
                    shortnames.ShouldContain("/orders/matched_order");
                    shortnames.ShouldContain("/orders/unmatched_order");
                    shortnames.Count.ShouldBe(2);
                    AssertMatchCount(resp.Records, "matched_order", expected: 1);
                    AssertMatchCount(resp.Records, "unmatched_order", expected: 0);
                    break;
                }
                case "inner":
                {
                    // Inner: unmatched_order is filtered out.
                    shortnames.ShouldContain("/orders/matched_order");
                    shortnames.ShouldNotContain("/orders/unmatched_order");
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
                    shortnames.ShouldContain("/orders/matched_order");
                    shortnames.ShouldNotContain("/orders/unmatched_order");
                    shortnames.ShouldContain("/customers/customer_b");
                    shortnames.ShouldContain("/customers/customer_c");
                    shortnames.ShouldNotContain("/customers/customer_a");
                    shortnames.Count.ShouldBe(3);
                    AssertMatchCount(resp.Records, "matched_order", expected: 1);
                    break;
                }
                case "outer":
                {
                    // Outer: both orders kept + appended unmatched rights.
                    shortnames.ShouldContain("/orders/matched_order");
                    shortnames.ShouldContain("/orders/unmatched_order");
                    shortnames.ShouldContain("/customers/customer_b");
                    shortnames.ShouldContain("/customers/customer_c");
                    shortnames.ShouldNotContain("/customers/customer_a");
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
