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

    // Resource snapshot fields, populated by EntryService.BuildEvent so the
    // SpaceEventLogger can serialize the same nested `resource` block Python
    // dmart's action_log writes (uuid + displayname/description/tags +
    // schema_shortname). Optional — when null, the writer either omits the
    // key (matching Python's pydantic JsonOmitNull) or emits null per shape.
    public string? Uuid { get; init; }
    public Translation? Displayname { get; init; }
    public Translation? Description { get; init; }
    public List<string>? Tags { get; init; }
}
