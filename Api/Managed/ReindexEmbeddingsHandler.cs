using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// POST /managed/reindex-embeddings — admin tool to (re)embed entries in
// bulk. Use cases:
//
//   * Backfill after activating `semantic_indexer` on a pre-existing space.
//   * Re-embed after changing EMBEDDING_MODEL (different dimensions break
//     queries until every vector is regenerated).
//   * Recover from embedding-provider outages that caused individual
//     writes to silently skip indexing.
//
// Body shape (all optional):
//   {
//     "space_name":   "<one space>",        // default: every space with semantic_indexer active
//     "only_missing": true,                 // default: true — skip rows that already have an embedding
//     "max_per_space": 5000                 // default: unbounded
//   }
//
// Returns counts: { spaces, scanned, embedded, skipped, failed }.
public static class ReindexEmbeddingsHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/reindex-embeddings", async Task<Response> (
            HttpRequest req, SemanticIndexerService svc,
            HttpContext http, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name;
            if (string.IsNullOrEmpty(actor))
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED,
                    "login required", "auth");

            string? space = null;
            var onlyMissing = true;
            int? maxPerSpace = null;

            if (req.ContentLength is > 0 || req.Headers.ContentType.Count > 0)
            {
                JsonDocument? doc;
                try { doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct); }
                catch (JsonException ex)
                {
                    return Response.Fail(InternalErrorCode.INVALID_DATA,
                        $"invalid body: {ex.Message}", "request");
                }
                using var _ = doc;
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in root.EnumerateObject())
                    {
                        switch (p.Name)
                        {
                            case "space_name":
                                if (p.Value.ValueKind == JsonValueKind.String) space = p.Value.GetString();
                                break;
                            case "only_missing":
                                onlyMissing = p.Value.ValueKind == JsonValueKind.True
                                    || (p.Value.ValueKind == JsonValueKind.False ? false : onlyMissing);
                                break;
                            case "max_per_space":
                                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
                                    maxPerSpace = n;
                                break;
                        }
                    }
                }
            }

            var stats = await svc.ReindexAllAsync(space, onlyMissing, maxPerSpace, ct);
            if (stats.Error is not null)
                return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                    $"reindex skipped: {stats.Error}", "request");

            return Response.Ok(attributes: new()
            {
                ["spaces"] = stats.Spaces,
                ["scanned"] = stats.Scanned,
                ["embedded"] = stats.Embedded,
                ["skipped"] = stats.Skipped,
                ["failed"] = stats.Failed,
            });
        });
}
