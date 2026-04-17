using Dmart.Config;
using Dmart.Models.Api;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Info;

// Returns the full public view of DmartSettings — every active setting the
// app is running with, except for secrets which are replaced with "***set***"
// when configured (so callers can confirm presence without seeing the value).
//
// Shares its projection with the `dmart settings` CLI subcommand via
// SettingsSerializer.ToPublicDictionary so the two views never drift.
public static class SettingsHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/settings", (IOptions<DmartSettings> opts) =>
            Response.Ok(attributes: SettingsSerializer.ToPublicDictionary(opts.Value)));
}
