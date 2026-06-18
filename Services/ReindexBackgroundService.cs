using System.Threading.Channels;

namespace Dmart.Services;

// A reindex job handed from the HTTP endpoint to the background worker. Stats is
// the live object ReindexJobTracker exposes for progress reads.
public sealed record ReindexRequest(string? Space, bool OnlyMissing, int? MaxPerSpace, ReindexStats Stats);

// Hosted worker that runs reindex jobs off the request thread AND drains cleanly
// on shutdown. Because the host awaits ExecuteAsync (within its shutdown
// timeout) after signalling stoppingToken, an in-flight sweep is cancelled and
// unwound before the singletons it uses (Db) are disposed — so shutdown no
// longer surfaces an ObjectDisposedException masquerading as "failed
// unexpectedly", and a long sweep gets a chance to stop at a page boundary.
//
// Single-flight is enforced by ReindexJobTracker.TryStart at the endpoint; the
// capacity-1 channel just transports the one accepted job to the worker.
public sealed class ReindexBackgroundService(
    SemanticIndexerService svc,
    ReindexJobTracker tracker,
    ILogger<ReindexBackgroundService> log) : BackgroundService
{
    // FullMode.Wait so TryWrite returns false (rather than silently dropping)
    // when the single slot is occupied — that lets TryEnqueue surface the rare
    // "couldn't hand off" case so the caller can release the tracker slot.
    private readonly Channel<ReindexRequest> _queue =
        Channel.CreateBounded<ReindexRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    // Hand an accepted job to the worker. Returns false only if the channel is
    // unexpectedly full — the tracker should already have rejected a concurrent
    // start, so a false here means the caller must release the tracker slot.
    public bool TryEnqueue(ReindexRequest req) => _queue.Writer.TryWrite(req);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var req in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var outcome = "completed";
                try
                {
                    log.LogInformation(
                        "semantic reindex started: space={Space} onlyMissing={OnlyMissing} maxPerSpace={Max}",
                        req.Space ?? "__all__", req.OnlyMissing, req.MaxPerSpace?.ToString() ?? "unbounded");

                    await svc.ReindexAllAsync(req.Space, req.OnlyMissing, req.MaxPerSpace, stoppingToken, req.Stats);

                    if (stoppingToken.IsCancellationRequested)
                    {
                        outcome = "cancelled";
                        log.LogInformation("semantic reindex stopped early (app shutting down)");
                    }
                    else if (req.Stats.Error is not null)
                    {
                        // TOCTOU: embeddings went unavailable between the endpoint's
                        // pre-flight check and the worker actually running.
                        outcome = "failed";
                        log.LogWarning("semantic reindex aborted: {Error}", req.Stats.Error);
                    }
                    else
                    {
                        log.LogInformation(
                            "semantic reindex finished: spaces={Spaces} scanned={Scanned} embedded={Embedded} skipped={Skipped} failed={Failed}",
                            req.Stats.Spaces, req.Stats.Scanned, req.Stats.Embedded, req.Stats.Skipped, req.Stats.Failed);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    outcome = "cancelled";
                    log.LogInformation("semantic reindex cancelled (app shutting down)");
                }
                catch (Exception ex)
                {
                    outcome = "failed";
                    log.LogError(ex, "semantic reindex failed unexpectedly");
                }
                finally
                {
                    tracker.Finish(outcome);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown while idle (blocked in ReadAllAsync). Nothing to drain.
        }
    }
}
