using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Managed;

public static class QueryHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/query", async Task (
            HttpRequest req, QueryService svc, HttpContext http,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            Query? q = null;
            Response resp;
            try
            {
                q = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.Query, ct);
                resp = q is null
                    ? Response.Fail(InternalErrorCode.INVALID_DATA, "empty body", ErrorTypes.Request)
                    : await svc.ExecuteAsync(q, http.Actor(), ct);
            }
            catch (JsonException)
            {
                resp = Response.Fail(InternalErrorCode.INVALID_DATA, "invalid request body", ErrorTypes.Request);
            }
            await JqEnvelope.WriteAsync(http.Response, resp, q?.JqFilter, settings.Value.JqTimeout, ct);
        })
        // The handler reads the body via HttpRequest so it can surface
        // malformed-JSON as dmart's structured failure envelope. Declare
        // the body type here so Swagger UI shows the Query schema and the
        // OpenApiExamples sample payload.
        .Accepts<Query>("application/json")
        .Produces<Response>();
}
