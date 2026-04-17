using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class QueryHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/query", async Task<Response> (HttpRequest req, QueryService svc, HttpContext http, CancellationToken ct) =>
        {
            Query? q;
            try
            {
                q = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.Query, ct);
            }
            catch (JsonException)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, "invalid request body", "request");
            }
            if (q is null)
                return Response.Fail(InternalErrorCode.INVALID_DATA, "empty body", "request");
            return await svc.ExecuteAsync(q, http.User.Identity?.Name, ct);
        });
}
