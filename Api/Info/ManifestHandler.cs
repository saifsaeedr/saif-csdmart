using Dmart.Models.Api;
using Dmart.Plugins;

namespace Dmart.Api.Info;

public static class ManifestHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/manifest", (PluginManager plugins) => Response.Ok(attributes: new()
        {
            ["name"] = "dmart",
            ["version"] = "0.1.0",
            ["api"] = "v1",
            // Matches dmart Python's /info/manifest — the list of shortnames
            // currently loaded by PluginManager after filtering on is_active.
            ["plugins"] = plugins.ActivePlugins.ToList(),
        }));
}
