using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// POST /managed/semantic-search — natural-language query → top-N similar
// entries. Requires pgvector + EMBEDDING_API_URL; returns a clean 400 error
// when either is missing.
//
// Body shape:
//   {
//     "query": "...",                (required)
//     "space_name": "...",           (optional)
//     "subpath": "/...",             (optional prefix)
//     "resource_types": ["content"], (optional)
//     "limit": 10                    (optional, default 10, max 100)
//   }
public static class SemanticSearchHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/semantic-search",
            async Task<Response> (HttpRequest req, SemanticSearchService svc,
                HttpContext http, CancellationToken ct) =>
            {
                JsonDocument? doc;
                try
                {
                    doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                }
                catch (JsonException ex)
                {
                    return Response.Fail(InternalErrorCode.INVALID_DATA,
                        $"invalid request body: {ex.Message}", ErrorTypes.Request);
                }
                using var _ = doc;
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Response.Fail(InternalErrorCode.INVALID_DATA,
                        "expected object body", ErrorTypes.Request);

                string? query = null;
                string? space = null;
                string? subpath = null;
                int limit = 10;
                List<ResourceType>? types = null;

                foreach (var p in root.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "query":
                            if (p.Value.ValueKind == JsonValueKind.String) query = p.Value.GetString();
                            break;
                        case "space_name":
                            if (p.Value.ValueKind == JsonValueKind.String) space = p.Value.GetString();
                            break;
                        case "subpath":
                            if (p.Value.ValueKind == JsonValueKind.String) subpath = p.Value.GetString();
                            break;
                        case "limit":
                            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var l))
                                limit = l;
                            break;
                        case "resource_types":
                            if (p.Value.ValueKind == JsonValueKind.Array)
                            {
                                types = [];
                                foreach (var el in p.Value.EnumerateArray())
                                {
                                    if (el.ValueKind == JsonValueKind.String &&
                                        Enum.TryParse<ResourceType>(el.GetString(), ignoreCase: true, out var rt))
                                        types.Add(rt);
                                }
                            }
                            break;
                    }
                }

                if (string.IsNullOrEmpty(query))
                    return Response.Fail(InternalErrorCode.MISSING_DATA,
                        "`query` is required", ErrorTypes.Request);

                return await svc.SearchAsync(query, space, subpath, types, limit,
                    http.Actor(), ct);
            });
}
