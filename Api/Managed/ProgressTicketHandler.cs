using Dmart.Api;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class ProgressTicketHandler
{
    public static void Map(RouteGroupBuilder g) =>
        // PUT /progress-ticket/{space}/{subpath:path}/{shortname}/{action}
        g.MapPut("/progress-ticket/{space}/{**rest}",
            async (string space, string rest,
                   WorkflowService wf, HttpContext http, CancellationToken ct) =>
            {
                var parts = RouteParts.SplitProgressTicketParts(rest);
                if (parts is null)
                    return Response.Fail(InternalErrorCode.MISSING_DATA,
                        "expected /progress-ticket/{space}/{subpath}/{shortname}/{action}", ErrorTypes.Request);
                var (subpath, shortname, action) = parts.Value;
                return await wf.ProgressAsync(
                    new Locator(ResourceType.Ticket, space, subpath, shortname),
                    action, http.Actor(), attrs: null, ct);
            });
}
