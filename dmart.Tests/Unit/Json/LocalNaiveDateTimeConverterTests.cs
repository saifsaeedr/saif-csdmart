using System;
using System.Globalization;
using System.Text.Json;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Json;

// Pins the Python-parity DateTime wire shape: server-local wall clock, NO
// offset (no `Z`, no `+03:00`). The DB columns are TIMESTAMP (without time
// zone); the converter never transforms — whatever DateTime it receives is
// emitted verbatim, regardless of Kind, and parsed strings come back as
// Kind=Unspecified with the exact value the wire carried.
public sealed class LocalNaiveDateTimeConverterTests
{
    private static JsonSerializerOptions Options() => new()
    {
        Converters = { new LocalNaiveDateTimeConverter() },
    };

    private const string Format = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Serializes_Verbatim_Regardless_Of_Kind(DateTimeKind kind)
    {
        // The same wall-clock value, labeled with each Kind, must produce
        // the same JSON output. No timezone projection anywhere.
        var value = new DateTime(2026, 4, 28, 2, 0, 0, kind).AddTicks(1234567);
        var expected = "\"" + value.ToString(Format, CultureInfo.InvariantCulture) + "\"";

        var json = JsonSerializer.Serialize(value, Options());

        json.ShouldBe(expected);
        // Belt-and-suspenders: never emit an offset suffix.
        json.ShouldNotContain("Z");
        json.ShouldNotMatch(@"[+-]\d{2}:\d{2}""$");
    }

    [Fact]
    public void Roundtrips_Naive_String_To_Unspecified()
    {
        // Python emits naive ISO strings; we parse them as wall-clock,
        // preserve the value byte-for-byte, and label Kind as Unspecified
        // so downstream code doesn't accidentally reapply a Local↔Utc shift.
        var pythonStyle = "2026-04-28T02:00:00.1234567";
        var json = "\"" + pythonStyle + "\"";

        var parsed = JsonSerializer.Deserialize<DateTime>(json, Options());

        parsed.Kind.ShouldBe(DateTimeKind.Unspecified);
        parsed.ShouldBe(new DateTime(2026, 4, 28, 2, 0, 0, DateTimeKind.Unspecified).AddTicks(1234567));
    }

    // The source-generated DmartJsonContext picks up the converter via
    // [JsonSourceGenerationOptions(Converters = ...)] — verify the wire
    // shape end-to-end through the context that production code uses.
    [Fact]
    public void DmartJsonContext_Serializes_Entry_CreatedAt_Without_Z_Or_Offset()
    {
        var entry = new Dmart.Models.Core.Entry
        {
            Uuid = "00000000-0000-0000-0000-000000000000",
            Shortname = "probe",
            SpaceName = "test",
            Subpath = "/",
            ResourceType = Dmart.Models.Enums.ResourceType.Content,
            OwnerShortname = "dmart",
            CreatedAt = new DateTime(2026, 4, 28, 2, 0, 0, DateTimeKind.Unspecified),
            UpdatedAt = new DateTime(2026, 4, 28, 2, 0, 0, DateTimeKind.Unspecified),
        };

        var json = JsonSerializer.Serialize(entry, DmartJsonContext.Default.Entry);

        json.ShouldContain("\"created_at\":");
        json.ShouldContain("\"updated_at\":");
        // No `Z` and no explicit offset in either timestamp.
        var doc = JsonDocument.Parse(json);
        var created = doc.RootElement.GetProperty("created_at").GetString()!;
        created.ShouldNotContain("Z");
        created.ShouldNotMatch(@"[+-]\d{2}:\d{2}$");
        // Wall-clock value emitted verbatim — no projection of any kind.
        created.ShouldStartWith("2026-04-28T02:00:00");
    }
}
