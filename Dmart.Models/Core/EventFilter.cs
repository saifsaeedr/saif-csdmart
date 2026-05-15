namespace Dmart.Models.Core;

// Plugin-event filter. Mirrors the shape of a permission row's scoping
// dimensions (see DmartPermission.Subpaths in Dmart.SqlAdapter), so an author
// who already understands permissions reads filters with no new vocabulary.
//
// Wildcard sentinels (consistent with the permission engine):
//   - Subpaths key   "__all_spaces__"   ⇒ match any space
//   - Subpaths value "__all_subpaths__" ⇒ match any subpath under that space
//   - Subpaths value containing "__current_user__" ⇒ resolved to the event's
//     user_shortname before matching (lets a plugin scope to a user's own
//     subtree, e.g. "users/__current_user__")
//
// Empty list semantics (also consistent with permissions):
//   - ResourceTypes empty    ⇒ match every resource_type
//   - SchemaShortnames empty ⇒ match every schema (when ResourceType is content)
//   - Actions empty          ⇒ match every action
//
// Subpath-walk semantics: a filter subpath of "tickets" matches event subpaths
// "tickets", "tickets/open", "tickets/open/p1" — i.e. the filter is a prefix
// gate, mirroring the permission engine's subpath walk.
public sealed record EventFilter
{
    // Keyed by space name (or "__all_spaces__"). Values are subpath patterns
    // (or "__all_subpaths__"). An empty dict means the plugin doesn't fire on
    // any event — explicitly opt in to "everything" via
    // { "__all_spaces__": ["__all_subpaths__"] }.
    public Dictionary<string, List<string>> Subpaths { get; init; } =
        new(StringComparer.Ordinal);

    public List<string> ResourceTypes { get; init; } = new();
    public List<string> SchemaShortnames { get; init; } = new();
    public List<string> Actions { get; init; } = new();
}
