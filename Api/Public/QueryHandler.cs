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
            Query? q = null;
            Response resp;
            try
            {
                q = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.Query, ct);
                // Python parity: /public/query resolves permissions under the
                // "anonymous" user row (+ optional "world" permission), so
                // anonymous queries see rows an admin configured as publicly
                // visible. We pass the identity explicitly so QueryService
                // builds row-level query_policies for anonymous — null would
                // skip the ACL filter entirely (internal-unrestricted path).
                // Public traffic skews read-heavy and most callers don't
                // use the `total` for pagination. The COUNT(*) runs in
                // parallel with the page query (QueryService.cs:354) and
                // doubles the DB load on every public request. Default to
                // false here so the count only fires when the caller asks
                // for it explicitly. Authenticated /managed/query keeps
                // the original null→true default.
                if (q is { RetrieveTotal: null })
                    q = q with { RetrieveTotal = false };
                resp = q is null
                    ? Response.Fail(InternalErrorCode.INVALID_DATA, "empty body", ErrorTypes.Request)
                    : await svc.ExecuteAsync(q, actor: "anonymous", ct);
            }
            catch (JsonException ex)
            {
                resp = Response.Fail(InternalErrorCode.INVALID_DATA,
                    $"invalid Query JSON: {ex.Message}", ErrorTypes.Request);
            }
            await JqEnvelope.WriteAsync(http.Response, resp, q?.JqFilter, settings.Value.JqTimeout, ct);
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
                // Same rationale as POST /public/query above — skip the
                // parallel COUNT by default; URL params can't request it.
                RetrieveTotal = false,
            };
            return await svc.ExecuteAsync(q, actor: null, ct);
        });
    }
}
