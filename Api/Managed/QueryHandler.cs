using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;
using Dmart.Utils;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Managed;

public static class QueryHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/query", async Task (
            HttpRequest req, QueryService svc, HttpContext http,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            Query? q;
            try
            {
                q = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.Query, ct);
            }
            catch (JsonException)
            {
                await WriteResponseAsync(http.Response,
                    Response.Fail(InternalErrorCode.INVALID_DATA, "invalid request body", ErrorTypes.Request), ct);
                return;
            }
            if (q is null)
            {
                await WriteResponseAsync(http.Response,
                    Response.Fail(InternalErrorCode.INVALID_DATA, "empty body", ErrorTypes.Request), ct);
                return;
            }

            var resp = await svc.ExecuteAsync(q, http.Actor(), ct);
            if (string.IsNullOrWhiteSpace(q.JqFilter))
            {
                await WriteResponseAsync(http.Response, resp, ct);
                return;
            }
            await JqEnvelope.WriteAsync(http.Response, resp, q.JqFilter, settings.Value.JqTimeout, ct);
        });

    private static Task WriteResponseAsync(HttpResponse http, Response resp, CancellationToken ct)
    {
        http.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(http.Body, resp, DmartJsonContext.Default.Response, ct);
    }
}
