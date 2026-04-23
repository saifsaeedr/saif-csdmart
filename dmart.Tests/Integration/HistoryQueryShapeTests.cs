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

// Pins the /managed/query?type=history response shape to Python parity:
//   - attributes carry: owner_shortname, timestamp, diff, last_checksum_history, space_name
//   - request_headers is NEVER emitted (Python strips it at adapter.py:3102)
//   - diff["password"] is masked as {"old":"********","new":"********"}
//   - diff[*].old / diff[*].new dicts have "headers" removed
public class HistoryQueryShapeTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public HistoryQueryShapeTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Update_Writes_PerField_OldNew_Diff_Shape_In_History()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var entrySvc = sp.GetRequiredService<EntryService>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var spaceName = "hshape_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = _factory.AdminShortname,
            IsActive = true, Languages = new() { Language.En },
            ActivePlugins = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var sn = "c_" + Guid.NewGuid().ToString("N")[..6];
        // Seed a ticket-ish entry so we can update a state field.
        var original = new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = sn, SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Ticket, IsActive = true,
            OwnerShortname = _factory.AdminShortname, State = "new",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await entryRepo.UpsertAsync(original);

        try
        {
            // Trigger an update — this is the path that Python writes history
            // on, and the only path that should produce a history row.
            var upd = await entrySvc.UpdateAsync(
                new Locator(ResourceType.Ticket, spaceName, "/", sn),
                new Dictionary<string, object> { ["state"] = "confirmed" },
                _factory.AdminShortname);
            upd.IsOk.ShouldBeTrue();

            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = spaceName,
                Subpath = "/",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();
            // Exactly one history row — the update. No create/delete/move rows.
            resp.Records!.Count.ShouldBe(1);

            var diff = (JsonElement)resp.Records[0].Attributes!["diff"]!;
            diff.ValueKind.ShouldBe(JsonValueKind.Object);
            // Every top-level diff value must be `{old, new}` and NOTHING else.
            foreach (var prop in diff.EnumerateObject())
            {
                prop.Value.ValueKind.ShouldBe(JsonValueKind.Object);
                var keys = prop.Value.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
                keys.ShouldBe(new[] { "new", "old" });
            }
            // The `state` transition we triggered must be present.
            diff.TryGetProperty("state", out var stateDiff).ShouldBeTrue();
            stateDiff.GetProperty("old").GetString().ShouldBe("new");
            stateDiff.GetProperty("new").GetString().ShouldBe("confirmed");
        }
        finally
        {
            try { await entryRepo.DeleteAsync(spaceName, "/", sn, ResourceType.Ticket); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
        }
    }

    [FactIfPg]
    public async Task Update_With_Unchanged_Array_Body_Does_Not_Show_In_Diff()
    {
        // Regression: resending `payload.body.items` verbatim (same array,
        // different serialization whitespace / key order / number format as
        // JSONB stores it) previously produced a phantom diff entry because
        // FlattenJson compared arrays via GetRawText(). The fix uses
        // structural equality.
        var sp = _factory.Services;
        _factory.CreateClient();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var entrySvc = sp.GetRequiredService<EntryService>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var spaceName = "unchn_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = _factory.AdminShortname,
            IsActive = true, Languages = new() { Language.En },
            ActivePlugins = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var sn = "t_" + Guid.NewGuid().ToString("N")[..6];

        // Seed a ticket with a payload.body containing an array-of-objects.
        // JSONB storage will reorder keys inside each item canonically.
        var seededBody = JsonSerializer.SerializeToElement(new
        {
            state = "pending",
            items = new object[]
            {
                new { sku = "A-1", qty = 1, price = 100 },
                new { sku = "B-2", qty = 3, price = 250 },
            },
        });
        await entryRepo.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = sn, SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Ticket, IsActive = true,
            OwnerShortname = _factory.AdminShortname, State = "new",
            Payload = new Payload { ContentType = ContentType.Json, Body = seededBody },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            // Send an update that changes `state` but re-sends the EXACT same
            // items array (possibly re-ordered keys). The diff must contain
            // `state` only — the items array must NOT appear.
            var patchBody = JsonSerializer.SerializeToElement(new
            {
                state = "confirmed",
                // Re-send items with inner keys in a DIFFERENT order than JSONB
                // would store (price before qty before sku). If flatten
                // compared via raw text, these would diff.
                items = new object[]
                {
                    new { price = 100, qty = 1, sku = "A-1" },
                    new { price = 250, qty = 3, sku = "B-2" },
                },
            });
            var patch = new Dictionary<string, object>
            {
                ["state"] = "confirmed",
                ["payload"] = new Dictionary<string, object>
                {
                    ["content_type"] = "json",
                    ["body"] = patchBody,
                },
            };
            var upd = await entrySvc.UpdateAsync(
                new Locator(ResourceType.Ticket, spaceName, "/", sn),
                patch, _factory.AdminShortname);
            upd.IsOk.ShouldBeTrue();

            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = spaceName,
                Subpath = "/",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();
            resp.Records!.Count.ShouldBe(1);
            var diff = (JsonElement)resp.Records[0].Attributes!["diff"]!;
            // Top-level state is the only key that should appear.
            diff.TryGetProperty("state", out _).ShouldBeTrue();
            diff.TryGetProperty("payload.body.items", out _).ShouldBeFalse(
                "unchanged payload.body.items must not appear in history_diff");
        }
        finally
        {
            try { await entryRepo.DeleteAsync(spaceName, "/", sn, ResourceType.Ticket); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
        }
    }

    [FactIfPg]
    public async Task History_Response_Includes_LastChecksum_And_Masks_Password_And_Strips_Headers()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var historyRepo = sp.GetRequiredService<HistoryRepository>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var spaceName = "hist_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = _factory.AdminShortname,
            IsActive = true, Languages = new() { Language.En },
            ActivePlugins = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var sn = "t_" + Guid.NewGuid().ToString("N")[..6];
        await entryRepo.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = sn, SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Content, IsActive = true,
            OwnerShortname = _factory.AdminShortname,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            // Seed history with: a password diff + a state diff with headers that
            // must be stripped. Both rows target the same entry.
            var diffWithPassword = new Dictionary<string, object>
            {
                ["password"] = new Dictionary<string, object> { ["old"] = "secret-a", ["new"] = "secret-b" },
            };
            await historyRepo.AppendAsync(spaceName, "/", sn, "dmart", null, diffWithPassword);

            var diffWithHeaders = new Dictionary<string, object>
            {
                ["state"] = new Dictionary<string, object>
                {
                    ["old"] = new Dictionary<string, object>
                    {
                        ["name"] = "new",
                        ["headers"] = new Dictionary<string, object> { ["UA"] = "curl" },
                    },
                    ["new"] = new Dictionary<string, object>
                    {
                        ["name"] = "confirmed",
                        ["headers"] = new Dictionary<string, object> { ["UA"] = "curl" },
                    },
                },
            };
            await historyRepo.AppendAsync(spaceName, "/", sn, "dmart", null, diffWithHeaders);

            // Query history via QueryService (skips HTTP auth flakes).
            var resp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = spaceName,
                Subpath = "/",
                FilterShortnames = new() { sn },
                Limit = 100,
            }, _factory.AdminShortname);

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();
            resp.Records!.Count.ShouldBeGreaterThanOrEqualTo(2);

            foreach (var rec in resp.Records)
            {
                rec.ResourceType.ShouldBe(ResourceType.History);
                rec.Attributes.ShouldNotBeNull();
                rec.Attributes!.ShouldContainKey("owner_shortname");
                rec.Attributes.ShouldContainKey("timestamp");
                rec.Attributes.ShouldContainKey("diff");
                rec.Attributes.ShouldContainKey("space_name");
                // request_headers must NEVER be present on the wire.
                rec.Attributes.ShouldNotContainKey("request_headers");
            }

            // Find the password row and verify it's masked.
            var pwdRec = resp.Records
                .Select(r => (r, (JsonElement)r.Attributes!["diff"]!))
                .FirstOrDefault(t => t.Item2.ValueKind == JsonValueKind.Object
                    && t.Item2.TryGetProperty("password", out _));
            pwdRec.r.ShouldNotBeNull();
            var pwdDiff = pwdRec.Item2.GetProperty("password");
            pwdDiff.GetProperty("old").GetString().ShouldBe("********");
            pwdDiff.GetProperty("new").GetString().ShouldBe("********");

            // Find the headers row and verify "headers" is stripped from old/new.
            var hdrRec = resp.Records
                .Select(r => (r, (JsonElement)r.Attributes!["diff"]!))
                .FirstOrDefault(t => t.Item2.ValueKind == JsonValueKind.Object
                    && t.Item2.TryGetProperty("state", out _));
            hdrRec.r.ShouldNotBeNull();
            var stateDiff = hdrRec.Item2.GetProperty("state");
            stateDiff.GetProperty("old").TryGetProperty("headers", out _).ShouldBeFalse();
            stateDiff.GetProperty("new").TryGetProperty("headers", out _).ShouldBeFalse();
            stateDiff.GetProperty("old").GetProperty("name").GetString().ShouldBe("new");
        }
        finally
        {
            try { await entryRepo.DeleteAsync(spaceName, "/", sn, ResourceType.Content); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
        }
    }
}
