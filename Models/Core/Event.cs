using Dmart.Models.Enums;

namespace Dmart.Models.Core;

// Mirrors dmart/backend/models/core.py::Event. This is what gets passed to a
// hook plugin's HookAsync() method and is the unit of before/after dispatch.
// Plugins filter against this via EventFilter (subpaths + resource_types +
// schema_shortnames + actions).
public sealed record Event
{
    public required string SpaceName { get; init; }
    public required string Subpath { get; init; }
    public string? Shortname { get; init; }
    public required ActionType ActionType { get; init; }
    public ResourceType? ResourceType { get; init; }
    public string? SchemaShortname { get; init; }
    public Dictionary<string, object> Attributes { get; init; } = new();
    public required string UserShortname { get; init; }
}
