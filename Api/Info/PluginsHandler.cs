using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Plugins;

namespace Dmart.Api.Info;

// GET /info/plugins — rich per-plugin listing (shortname, version, type).
//
// Sister endpoint to /info/manifest, which keeps its existing
// `attributes.plugins: ["name1","name2"]` shape for back-compat. Operators
// who want versioned plugin metadata should call /info/plugins.
//
// The version comes from the plugin binary itself (see PluginManager.ResolveVersion):
//   - In-process .NET plugins → AssemblyInformationalVersion of the loaded
//     assembly. Built-in plugins (Plugins/BuiltIn/*) ship inside the dmart
//     binary so they inherit dmart's own version.
//   - Native .so plugins → string returned from the optional `dmart_plugin_version`
//     export, baked into the .so's .rodata at compile time.
//   - Subprocess plugins → `version` field on the {"type":"info"} response,
//     which the plugin author bakes into their build artifact (Go ldflags,
//     Python __version__, etc.).
//   - Unknown / undeclared → "0.0.0" sentinel.
public static class PluginsHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/plugins", (PluginManager plugins) =>
            Response.Ok(records: plugins.ActivePluginInfos
                .Select(p => new Record
                {
                    ResourceType = ResourceType.PluginWrapper,
                    Shortname = p.Shortname,
                    Subpath = "/",
                    Attributes = new Dictionary<string, object>
                    {
                        ["version"] = p.Version,
                        ["type"] = p.Type,
                    },
                })
                .ToList()));
}
