using System;
using System.Collections.Generic;
using System.Text.Json;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Json;

// DmartJsonContext is the single source of truth for every JSON wire shape
// the server emits under AOT. The scalar registrations at the bottom of the
// context aren't cosmetic — without them, source-gen throws
// NotSupportedException at runtime for any ValueType it hasn't been told
// about, as soon as a specific payload happens to carry that type inside a
// Dictionary<string, object> bag.
//
// Two prior commits wired these in:
//   dd68c9e — long, int, bool (seen on RequestLoggingMiddleware body capture)
//   2d5c356 — DateTime, DateTimeOffset, Guid (preventive for attribute bags)
//
// These tests lock in that coverage: removing a [JsonSerializable(typeof(...))]
// registration now fails here rather than 500-ing when a specific payload
// carries that scalar type through an access log or attribute dict.
public sealed class DmartJsonContextScalarTests
{
    // Parameterized over every registered scalar — one failure per type
    // surfaces cleanly in the runner output.
    [Theory]
    [InlineData(42L)]
    [InlineData(42)]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(3.14)]
    [InlineData("text")]
    public void Registered_Scalar_In_Dictionary_Object_Serializes(object value)
    {
        var bag = new Dictionary<string, object> { ["v"] = value };
        var json = JsonSerializer.Serialize(bag, DmartJsonContext.Default.DictionaryStringObject);
        json.ShouldNotBeNull();
        json.ShouldContain("\"v\":");
    }

    [Fact]
    public void DateTime_In_Dictionary_Object_Serializes()
    {
        // DateTime also lives on Entry.CreatedAt/UpdatedAt but source-gen
        // only knows about it via the explicit [JsonSerializable(typeof(DateTime))]
        // when it appears boxed inside Dictionary<string, object>.
        var bag = new Dictionary<string, object>
        {
            ["ts"] = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc),
        };
        var json = JsonSerializer.Serialize(bag, DmartJsonContext.Default.DictionaryStringObject);
        json.ShouldContain("\"ts\":");
    }

    [Fact]
    public void DateTimeOffset_In_Dictionary_Object_Serializes()
    {
        var bag = new Dictionary<string, object>
        {
            ["ts"] = new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.FromHours(3)),
        };
        var json = JsonSerializer.Serialize(bag, DmartJsonContext.Default.DictionaryStringObject);
        json.ShouldContain("\"ts\":");
    }

    [Fact]
    public void Guid_In_Dictionary_Object_Serializes()
    {
        var bag = new Dictionary<string, object>
        {
            ["id"] = Guid.Parse("d47a0b4a-8d83-4c2f-99b3-0e3f16b4b5c1"),
        };
        var json = JsonSerializer.Serialize(bag, DmartJsonContext.Default.DictionaryStringObject);
        json.ShouldContain("d47a0b4a-8d83-4c2f-99b3-0e3f16b4b5c1");
    }

    [Fact]
    public void Mixed_Scalar_Bag_Roundtrips()
    {
        // Mirrors the real access-log shape — one Dictionary<string, object>
        // carrying multiple runtime scalar types in one call.
        var bag = new Dictionary<string, object>
        {
            ["pid"]     = 12345,
            ["enabled"] = true,
            ["ratio"]   = 0.95,
            ["created"] = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc),
            ["uuid"]    = Guid.Parse("d47a0b4a-8d83-4c2f-99b3-0e3f16b4b5c1"),
            ["name"]    = "hello",
        };

        var json = JsonSerializer.Serialize(bag, DmartJsonContext.Default.DictionaryStringObject);
        json.ShouldNotBeNullOrEmpty();

        var back = JsonSerializer.Deserialize(json, DmartJsonContext.Default.DictionaryStringObject);
        back.ShouldNotBeNull();
        back!.Count.ShouldBe(6);
    }
}
