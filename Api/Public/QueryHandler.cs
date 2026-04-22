using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;
using Dmart.Utils;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Public;

public static class QueryHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Read body as raw stream and deserialize ourselves so we can surface the
        // real JSON error (Minimal APIs swallow body-binding errors as 400 no-body).
        g.MapPost("/query", async Task (
            HttpRequest req, QueryService svc, HttpContext http,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            Query? q;
            try
            {
                q = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.Query, ct);
            }
            catch (JsonException ex)
            {
                await WriteResponseAsync(http.Response,
                    Response.Fail(InternalErrorCode.INVALID_DATA,
                        $"invalid Query JSON: {ex.Message}", ErrorTypes.Request), ct);
                return;
            }
            if (q is null)
            {
                await WriteResponseAsync(http.Response,
                    Response.Fail(InternalErrorCode.INVALID_DATA, "empty body", ErrorTypes.Request), ct);
                return;
            }
            // Python parity: /public/query resolves permissions under the
            // "anonymous" user row (+ optional "world" permission), so
            // anonymous queries see rows an admin configured as publicly
            // visible. We pass the identity explicitly so QueryService
            // builds row-level query_policies for anonymous — null would
            // skip the ACL filter entirely (internal-unrestricted path).
            var resp = await svc.ExecuteAsync(q, actor: "anonymous", ct);
            if (string.IsNullOrWhiteSpace(q.JqFilter))
            {
                await WriteResponseAsync(http.Response, resp, ct);
                return;
            }
            await JqEnvelope.WriteAsync(http.Response, resp, q.JqFilter, settings.Value.JqTimeout, ct);
        });

        // Python: GET /public/query-via-url — query via URL parameters (for embedding).
        // Also: GET /public/query/{type}/{space_name}/{subpath}
        g.MapGet("/query/{type}/{space_name}/{subpath}", async (
            string type, string space_name, string subpath,
            int? limit, int? offset, string? search,
            QueryService svc, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Dmart.Models.Enums.QueryType>(type, ignoreCase: true, out var qt))
                return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                    $"unknown query type: {type}", ErrorTypes.Request);
            var q = new Query
            {
                Type = qt,
                SpaceName = space_name,
                Subpath = subpath,
                Limit = limit ?? 10,
                Offset = offset ?? 0,
                Search = search,
            };
            return await svc.ExecuteAsync(q, actor: null, ct);
        });
    }

    private static Task WriteResponseAsync(HttpResponse http, Response resp, CancellationToken ct)
    {
        http.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(http.Body, resp, DmartJsonContext.Default.Response, ct);
    }
}
