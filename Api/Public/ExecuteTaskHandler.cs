using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Api.Public;

// Python: POST /public/excute/{task_type}/{space} — executes a saved query task
// (same as managed but unauthenticated, limited to query type only).
public static class ExecuteTaskHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/excute/{task_type}/{space_name}", async (
            string task_type, string space_name,
            Query q, QueryService queryService, CancellationToken ct) =>
        {
            if (task_type != "query")
                return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                    $"public task type '{task_type}' not supported", "request");
            var adjusted = q with { SpaceName = space_name };
            return await queryService.ExecuteAsync(adjusted, null, ct);
        });
}
