namespace Dmart.Api.Managed;

public static class ManagedEndpoints
{
    public static RouteGroupBuilder MapManaged(this RouteGroupBuilder g)
    {
        QueryHandler.Map(g);
        SemanticSearchHandler.Map(g);
        RequestHandler.Map(g);
        EntryHandler.Map(g);
        PayloadHandler.Map(g);
        LockHandler.Map(g);
        ProgressTicketHandler.Map(g);
        CsvHandler.Map(g);
        ImportExportHandler.Map(g);
        ResourceWithPayloadHandler.Map(g);
        HealthHandler.Map(g);
        ExecuteTaskHandler.Map(g);
        AlterationHandler.Map(g);
        ShortLinkHandler.Map(g);
        return g;
    }
}
