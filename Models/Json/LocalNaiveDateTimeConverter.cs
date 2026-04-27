using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dmart.Models.Json;

// JSON converter that mirrors Python dmart_plain's pydantic naive output:
// `created_at`/`updated_at` etc. emit as `2026-04-26T07:13:14.123456` —
// server-local wall clock, NO `Z`, NO `+03:00` offset. Matches Python
// `datetime.now().isoformat()` on a naive datetime byte-for-byte.
//
// Wall-clock-only contract (Python parity): the DB columns are TIMESTAMP
// (no time zone), the application thinks in local wall clock end-to-end,
// and there is NO conversion anywhere — not on the wire, not on read,
// not on write. Whatever value Npgsql hands us, we emit verbatim; whatever
// string the client sends, we parse verbatim. DateTimeKind is irrelevant
// to the wire shape and we preserve it as Unspecified on parse.
public sealed class LocalNaiveDateTimeConverter : JsonConverter<DateTime>
{
    // Matches Python's isoformat() output. `FFFFFFF` trims trailing zeros
    // in the fractional seconds (Python prints up to 6 digits and elides
    // trailing zeros via str(microsecond)); .NET's `FFFFFFF` gives the
    // same elision up to 7 digits. The wire shape stays ISO-parseable.
    private const string Format = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s))
            throw new JsonException("Expected a non-empty datetime string");
        // No tz adjustment: parse the wall-clock value as-is and stamp Kind
        // as Unspecified so downstream code doesn't accidentally retrigger
        // a Local↔Utc conversion.
        var parsed = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
}
