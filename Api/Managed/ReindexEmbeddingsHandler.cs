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
// Returns immediately with 200. The job runs in the background tied to app
// lifetime. Only one job may run at a time; concurrent requests get
// LOCK_UNAVAILABLE. Only global admins may start a reindex.
public static class ReindexEmbeddingsHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/reindex-embeddings", async Task<Response> (
            HttpRequest req, SemanticIndexerService svc,
            PermissionService perms, ReindexJobTracker tracker,
            HttpContext http, IHostApplicationLifetime lifetime,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (string.IsNullOrEmpty(actor))
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED,
                    "login required", ErrorTypes.Auth);

            if (!await perms.IsGlobalAdminAsync(actor, ct))
                return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                    "not allowed — global admin required", ErrorTypes.Request);

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
                        $"invalid body: {ex.Message}", ErrorTypes.Request);
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

            // Quick pre-flight: confirm embeddings are configured before accepting the job.
            var check = await svc.CheckEnabledAsync(ct);
            if (check is not null)
                return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                    $"reindex skipped: {check}", ErrorTypes.Request);

            var liveStats = tracker.TryStart(space);
            if (liveStats is null)
            {
                // A job is already running — return its live progress instead of an error.
                var running = tracker.RunningStatus;
                return Response.Ok(attributes: new()
                {
                    ["status"]     = "already_running",
                    ["space"]      = running?.Space ?? "__all__",
                    ["started_at"] = (object?)running?.StartedAt ?? "unknown",
                    ["scanned"]    = running?.Stats.Scanned ?? 0,
                    ["embedded"]   = running?.Stats.Embedded ?? 0,
                    ["skipped"]    = running?.Stats.Skipped ?? 0,
                    ["failed"]     = running?.Stats.Failed ?? 0,
                    ["spaces_done"] = running?.Stats.Spaces ?? 0,
                });
            }

            var log = loggerFactory.CreateLogger("Dmart.Api.Managed.ReindexEmbeddings");
            var appStopping = lifetime.ApplicationStopping;

            // Run detached from the HTTP request so a client timeout or disconnect
            // can't cancel mid-sweep. Tied to app lifetime so it stops on shutdown.
            _ = Task.Run(async () =>
            {
                try
                {
                    log.LogInformation("semantic reindex started: space={Space} onlyMissing={OnlyMissing} maxPerSpace={Max}",
                        space ?? "__all__", onlyMissing, maxPerSpace?.ToString() ?? "unbounded");
                    await svc.ReindexAllAsync(space, onlyMissing, maxPerSpace, appStopping, liveStats);
                    log.LogInformation("semantic reindex finished: spaces={Spaces} scanned={Scanned} embedded={Embedded} skipped={Skipped} failed={Failed}",
                        liveStats.Spaces, liveStats.Scanned, liveStats.Embedded, liveStats.Skipped, liveStats.Failed);
                }
                catch (OperationCanceledException)
                {
                    log.LogInformation("semantic reindex cancelled (app shutting down)");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "semantic reindex failed unexpectedly");
                }
                finally
                {
                    tracker.Finish();
                }
            }, CancellationToken.None);

            return Response.Ok(attributes: new() { ["status"] = "started" });
        })
        .Accepts<Dmart.Models.Api.ReindexEmbeddingsBody>("application/json")
        .Produces<Response>();
}
