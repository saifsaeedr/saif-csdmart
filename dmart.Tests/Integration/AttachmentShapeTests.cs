using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the wire shape of attachments in /managed/entry and /managed/query:
// Python parity — each attachment dict is `{resource_type, uuid, shortname,
// subpath, attributes: {...meta fields...}}`. Previously EntryHandler.cs
// explicitly stripped the `attributes` wrapper and spread its contents at
// the record root, so clients parsing attachment.attributes.X got
// `undefined`. The /query path was always correct.
public sealed class AttachmentShapeTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public AttachmentShapeTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task AttachmentMapper_ToEntryRecord_Serializes_With_Attributes_Wrapper()
    {
        // Exercises the same serialization path EntryHandler now uses after
        // dropping the flatten loop. Sidesteps auth / permissions by calling
        // the mapper + JsonSerializer directly.
        _ = _factory.Services; // touch services for fixture parity
        await Task.CompletedTask;

        var att = new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "c1",
            SpaceName = "s1",
            Subpath = "/parent",
            ResourceType = ResourceType.Comment,
            IsActive = true,
            OwnerShortname = "dmart",
            Body = "hello",
            State = "initial",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var rec = AttachmentMapper.ToEntryRecord(att);
        var json = JsonSerializer.Serialize(rec, DmartJsonContext.Default.Record);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Root-level keys (Python parity).
        root.TryGetProperty("resource_type", out _).ShouldBeTrue(json);
        root.TryGetProperty("shortname", out _).ShouldBeTrue(json);
        root.TryGetProperty("subpath", out _).ShouldBeTrue(json);
        root.TryGetProperty("uuid", out _).ShouldBeTrue(json);

        // CRITICAL: attributes is a nested object, not spread at root.
        root.TryGetProperty("attributes", out var attrs).ShouldBeTrue(
            "attachment record must carry an `attributes` wrapper — Python parity");
        attrs.ValueKind.ShouldBe(JsonValueKind.Object);
        attrs.TryGetProperty("body", out var body).ShouldBeTrue();
        body.GetString().ShouldBe("hello");
        attrs.TryGetProperty("state", out var state).ShouldBeTrue();
        state.GetString().ShouldBe("initial");
        attrs.TryGetProperty("is_active", out var isActive).ShouldBeTrue();
        isActive.GetBoolean().ShouldBeTrue();

        // Flat-shape leak check — no meta field should appear at record root.
        root.TryGetProperty("body", out _).ShouldBeFalse(
            "attribute fields must NOT be spread at record root");
        root.TryGetProperty("state", out _).ShouldBeFalse();
        root.TryGetProperty("is_active", out _).ShouldBeFalse();
    }

    [FactIfPg]
    public void ManagedEntry_Attachment_JsonNode_Round_Trip_Keeps_Attributes()
    {
        // Verifies the new EntryHandler path: serialize Record → parse as
        // JsonNode → add straight into the attachments array. The Record must
        // NOT be mutated to strip `attributes`. Mirrors the production code at
        // EntryHandler.cs:90-104.
        var att = new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "m1",
            SpaceName = "s1",
            Subpath = "/parent",
            ResourceType = ResourceType.Media,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var rec = AttachmentMapper.ToEntryRecord(att);
        var recJson = JsonSerializer.Serialize(rec, DmartJsonContext.Default.Record);
        var recNode = JsonNode.Parse(recJson)!.AsObject();

        recNode["attributes"].ShouldNotBeNull();
        recNode["attributes"]!.GetValueKind().ShouldBe(JsonValueKind.Object);
        // Before the fix, EntryHandler removed "attributes" here. Confirm the
        // wrapper survives the round-trip.
        var attrObj = recNode["attributes"] as JsonObject;
        attrObj.ShouldNotBeNull();
        attrObj!.ContainsKey("is_active").ShouldBeTrue();
    }
}
