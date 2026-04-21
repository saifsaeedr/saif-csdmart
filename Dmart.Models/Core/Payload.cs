using System.Text.Json;
using Dmart.Models.Enums;

namespace Dmart.Models.Core;

// Mirrors dmart/backend/models/core.py::Payload exactly.
//
// dmart's `body` is `str | dict[str, Any] | None`. We use JsonElement so the C# side
// can carry either form losslessly without forcing a schema, and round-trip back to
// jsonb without needing source-gen metadata for every nested value type.
public sealed record Payload
{
    public ContentType ContentType { get; init; }
    public string? ContentSubType { get; init; }
    public string? SchemaShortname { get; init; }
    public string? ClientChecksum { get; init; }
    public string? Checksum { get; init; }
    public JsonElement? Body { get; init; }
}
