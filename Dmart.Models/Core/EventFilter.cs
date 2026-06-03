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
    // A property initializer alone does NOT keep these non-null: System.Text.Json
    // source-gen calls the setter (overwriting the default) when a key is present
    // but null — so `"schema_shortnames": null` in config.json yields a null list.
    // Each member below coalesces null → empty in its init accessor, making the
    // non-nullable type honest at runtime. That invariant lives on the type so
    // every read site can treat these as never-null without its own guard, and so
    // both plugin-config deserialization paths (PluginManager + NativePluginLoader)
    // are covered by construction rather than by remembering to guard each one.

    // Keyed by space name (or "__all_spaces__"). Values are subpath patterns
    // (or "__all_subpaths__"). An empty dict means the plugin doesn't fire on
    // any event — explicitly opt in to "everything" via
    // { "__all_spaces__": ["__all_subpaths__"] }.
    private readonly Dictionary<string, List<string>> _subpaths = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Subpaths
    {
        get => _subpaths;
        init => _subpaths = value ?? new(StringComparer.Ordinal);
    }

    private readonly List<string> _resourceTypes = new();
    public List<string> ResourceTypes
    {
        get => _resourceTypes;
        init => _resourceTypes = value ?? new();
    }

    private readonly List<string> _schemaShortnames = new();
    public List<string> SchemaShortnames
    {
        get => _schemaShortnames;
        init => _schemaShortnames = value ?? new();
    }

    private readonly List<string> _actions = new();
    public List<string> Actions
    {
        get => _actions;
        init => _actions = value ?? new();
    }
}
