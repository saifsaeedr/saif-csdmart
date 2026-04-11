using Dmart.Models.Enums;

namespace Dmart.Models.Core;

// Mirrors dmart/backend/models/core.py::PluginWrapper. This is the shape of a
// plugin's config.json on disk. The "object" field from Python (the actual
// plugin instance) is not part of the wire form — PluginManager attaches the
// C# instance at load time via a separate lookup.
//
// Note: `is_active` controls whether the plugin is loaded at all; the per-space
// `active_plugins` list on Space then decides whether this plugin fires for
// events coming from that space.
public sealed record PluginWrapper
{
    public string Shortname { get; set; } = "";
    public bool IsActive { get; init; }
    public EventFilter? Filters { get; init; }
    public EventListenTime? ListenTime { get; init; }
    public PluginType? Type { get; init; }
    public int Ordinal { get; init; } = 9999;
    public List<string> Dependencies { get; init; } = new();
    public bool Concurrent { get; init; } = true;
    public Dictionary<string, object>? Attributes { get; init; }
}
