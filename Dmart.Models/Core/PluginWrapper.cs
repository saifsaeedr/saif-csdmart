using Dmart.Models.Enums;

namespace Dmart.Models.Core;

// Mirrors dmart/backend/models/core.py::PluginWrapper. This is the shape of a
// plugin's config.json on disk. The "object" field from Python (the actual
// plugin instance) is not part of the wire form — PluginManager attaches the
// C# instance at load time via a separate lookup.
//
// Activation layers (each gates the next):
//   1. `is_active`       — is the plugin loaded at all?
//   2. Space `active_plugins` — does THIS space want this plugin to fire?
//   3. `always_active`   — if true, skip layer 2 and fire on every space's
//                          events regardless. Use sparingly — only for
//                          system-level behavior (e.g. semantic_indexer) that
//                          should apply uniformly. C# extension to the Python
//                          shape; Python currently ignores it.
public sealed record PluginWrapper
{
    public string Shortname { get; set; } = "";
    public bool IsActive { get; init; }
    public bool AlwaysActive { get; init; }
    public EventFilter? Filters { get; init; }
    public EventListenTime? ListenTime { get; init; }
    public PluginType? Type { get; init; }
    public int Ordinal { get; init; } = 9999;
    public List<string> Dependencies { get; init; } = new();
    public bool Concurrent { get; init; } = true;
    public Dictionary<string, object>? Attributes { get; init; }
}
