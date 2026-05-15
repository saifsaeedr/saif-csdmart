namespace Dmart.Api.Info;

public static class InfoEndpoints
{
    public static RouteGroupBuilder MapInfo(this RouteGroupBuilder g)
    {
        MeHandler.Map(g);
        SettingsHandler.Map(g);
        ManifestHandler.Map(g);
        PluginsHandler.Map(g);
        return g;
    }
}
