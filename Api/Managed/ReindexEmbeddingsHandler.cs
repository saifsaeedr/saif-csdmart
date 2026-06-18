using System.Text.Json;
using Dmart.Models.Api;
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
// Returns immediately with 200 {status:"started"}. The job runs on a hosted
// BackgroundService (drained on shutdown), not the request thread. Only one job
// may run at a time; a concurrent POST returns 200 with the running job's live
// progress ({status:"already_running", ...}) rather than starting a second.
// Only global admins may start a reindex or read status.
//
// GET /managed/reindex-embeddings/status — admin-only. Returns the live progress
// of a running job, else the last finished run's summary, else {status:"idle"}.
public static class ReindexEmbeddingsHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/reindex-embeddings", async Task<Response> (
            HttpRequest req, SemanticIndexerService svc,
            PermissionService perms, ReindexJobTracker tracker, ReindexBackgroundService worker,
            HttpContext http, CancellationToken ct) =>
        {
            var denied = await RequireAdminAsync(http, perms, ct);
            if (denied is not null) return denied;

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
                // A job is already running — report its live progress instead of an error.
                return StatusResponse(tracker, runningLabel: "already_running");

            if (!worker.TryEnqueue(new ReindexRequest(space, onlyMissing, maxPerSpace, liveStats)))
            {
                // The tracker already gated concurrent starts, so a full queue here is
                // unexpected — release the slot we just took so it can't get stuck.
                tracker.Finish("failed");
                return Response.Fail(InternalErrorCode.LOCK_UNAVAILABLE,
                    "reindex worker is busy, try again shortly", ErrorTypes.Request);
            }

            return Response.Ok(attributes: new() { ["status"] = "started" });
        })
        .Accepts<Dmart.Models.Api.ReindexEmbeddingsBody>("application/json")
        .Produces<Response>();

        g.MapGet("/reindex-embeddings/status", async Task<Response> (
            PermissionService perms, ReindexJobTracker tracker,
            HttpContext http, CancellationToken ct) =>
        {
            var denied = await RequireAdminAsync(http, perms, ct);
            if (denied is not null) return denied;
            return StatusResponse(tracker);
        })
        .Produces<Response>();
    }

    // Returns a Failed response when the caller isn't a logged-in global admin,
    // or null when access is granted.
    private static async Task<Response?> RequireAdminAsync(
        HttpContext http, PermissionService perms, CancellationToken ct)
    {
        var actor = http.Actor();
        if (string.IsNullOrEmpty(actor))
            return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);
        if (!await perms.IsGlobalAdminAsync(actor, ct))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                "not allowed — global admin required", ErrorTypes.Auth);
        return null;
    }

    // Live progress if a job is running, else the last finished run's summary,
    // else idle. `runningLabel` overrides the running status string so the POST
    // conflict path can say "already_running" while GET says "running".
    private static Response StatusResponse(ReindexJobTracker tracker, string runningLabel = "running")
    {
        var cur = tracker.Current;
        if (cur is not null)
            return Response.Ok(attributes: new()
            {
                ["status"]      = runningLabel,
                ["space"]       = cur.Space,
                ["started_at"]  = cur.StartedAt.ToString("o"),
                ["scanned"]     = cur.Stats.Scanned,
                ["embedded"]    = cur.Stats.Embedded,
                ["skipped"]     = cur.Stats.Skipped,
                ["failed"]      = cur.Stats.Failed,
                ["spaces_done"] = cur.Stats.Spaces,
            });

        var last = tracker.LastRun;
        if (last is not null)
            return Response.Ok(attributes: new()
            {
                ["status"]      = last.Outcome,   // completed / cancelled / failed
                ["space"]       = last.Space,
                ["started_at"]  = last.StartedAt.ToString("o"),
                ["finished_at"] = last.FinishedAt.ToString("o"),
                ["scanned"]     = last.Scanned,
                ["embedded"]    = last.Embedded,
                ["skipped"]     = last.Skipped,
                ["failed"]      = last.Failed,
                ["spaces_done"] = last.Spaces,
                ["error"]       = (object?)last.Error ?? "",
            });

        return Response.Ok(attributes: new() { ["status"] = "idle" });
    }
}
