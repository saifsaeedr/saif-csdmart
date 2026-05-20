using Dmart.Models.Api;
using Dmart.Services;

namespace Dmart.Api.Public;

// Python: POST /public/excute/{task_type}/{space} — executes a saved query task
// (same as managed but unauthenticated, limited to query type only).
public static class ExecuteTaskHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/excute/{task_type}/{space_name}", async (
            string task_type, string space_name,
            HttpRequest req, EntryService entries, QueryService queryService,
            CancellationToken ct) =>
            await Dmart.Api.Managed.ExecuteTaskHandler.ExecuteFromBodyAsync(
                task_type, space_name, req, entries, queryService, "anonymous", ct))
            .Accepts<ExecuteTaskBody>("application/json")
            .Produces<Response>();
}
