namespace Dmart.Plugins;

// Mirrors dmart's `type: api` plugin variant. An API plugin declares its
// shortname and wires its own routes into a RouteGroupBuilder rooted at
// /{Shortname}. PluginManager mounts it during startup.
//
// Keeping this a narrow interface instead of a MapGet-style convention lets
// individual plugins decide their own verbs + auth policies.
public interface IApiPlugin
{
    string Shortname { get; }
    void MapRoutes(RouteGroupBuilder group);
}
