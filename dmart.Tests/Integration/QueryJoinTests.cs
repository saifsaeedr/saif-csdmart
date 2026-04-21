using System.Collections.Generic;
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
                ActivePlugins = new(),
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
