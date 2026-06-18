using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Npgsql;

namespace Dmart.Api;

// Liveness/readiness probes for orchestrators (Kubernetes, blue-green). Both
// are auth-exempt (a probe carries no JWT) and unmetered. They return the
// canonical dmart Response envelope so scrape/log tooling sees a uniform wire
// shape; probes themselves only read the HTTP status code.
//
// Distinct from /managed/health/{type}/{space}, which validates entry data —
// not a process probe. These live at the root, outside Python's route space.
public static class HealthEndpoints
{
    // Bound independently of the global request-timeout middleware: an
    // orchestrator needs a fast verdict, not a 35s hang on a wedged DB.
    private static readonly TimeSpan ReadyProbeTimeout = TimeSpan.FromSeconds(3);

    public static void MapHealth(this WebApplication app)
    {
        // Process is up; no I/O. Cannot fail unless the process is gone.
        app.MapGet("/health/live", () =>
            Results.Json(Response.Ok(), DmartJsonContext.Default.Response))
            .AllowAnonymous().ExcludeFromDescription().WithTags("Health");

        // Process is up AND PostgreSQL answers a trivial query. 503 takes the
        // node out of rotation when the DB is unreachable.
        app.MapGet("/health/ready", async (Db db, CancellationToken ct) =>
        {
            if (!db.IsConfigured)
                return NotReady("database not configured");

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ReadyProbeTimeout);
                await using var conn = await db.OpenAsync(cts.Token);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return NotReady("database unreachable");
            }

            return Results.Json(Response.Ok(), DmartJsonContext.Default.Response);
        }).AllowAnonymous().ExcludeFromDescription().WithTags("Health");
    }

    private static IResult NotReady(string message) =>
        Results.Json(
            Response.Fail(InternalErrorCode.SOMETHING_WRONG, message, ErrorTypes.Db),
            DmartJsonContext.Default.Response, statusCode: 503);
}
