namespace Dmart.Services;

// Singleton gate: at most one reindex job runs at a time, with live progress
// and a retained summary of the last completed run.
//
// Thread-safety model:
//   * TryStart / Finish use Interlocked.CompareExchange on _running, so two
//     concurrent HTTP requests can't both win the slot.
//   * Per-run state (space, start time, stats) is published as a single
//     immutable Snapshot through one volatile field. A status read therefore
//     never observes a torn mix of old/new fields. The int counters inside the
//     Snapshot's ReindexStats are incremented by the single worker thread and
//     read (atomically, possibly slightly stale) by status threads — fine for a
//     progress display.
public sealed class ReindexJobTracker
{
    // Live view of an in-flight job.
    public sealed record Snapshot(string Space, DateTimeOffset StartedAt, ReindexStats Stats);

    // Frozen summary of a finished job, kept until the next run replaces it.
    public sealed record RunSummary(
        string Space, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Outcome,
        int Spaces, int Scanned, int Embedded, int Skipped, int Failed, string? Error);

    private volatile int _running;             // 0 = idle, 1 = running
    private volatile Snapshot? _current;       // published atomically by TryStart
    private volatile RunSummary? _lastRun;     // survives across runs for status reads

    // Atomically acquire the slot and publish a fresh snapshot. Returns the live
    // ReindexStats to pass into ReindexAllAsync, or null when a job is running.
    public ReindexStats? TryStart(string? space)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return null;
        var stats = new ReindexStats();
        _current = new Snapshot(space ?? "__all__", DateTimeOffset.UtcNow, stats);
        return stats;
    }

    // Release the slot, freezing the run's final state as the last-run summary.
    // Call from a finally so a failure always clears the flag. `outcome` is one
    // of "completed" / "cancelled" / "failed".
    public void Finish(string outcome)
    {
        var snap = _current;
        if (snap is not null)
        {
            var s = snap.Stats;
            _lastRun = new RunSummary(
                snap.Space, snap.StartedAt, DateTimeOffset.UtcNow, outcome,
                s.Spaces, s.Scanned, s.Embedded, s.Skipped, s.Failed, s.Error);
        }
        _current = null;
        Interlocked.Exchange(ref _running, 0);
    }

    public bool IsRunning => _running == 1;

    // Live snapshot of the in-flight job, or null when idle. Reading the single
    // volatile reference keeps the (space, startedAt, stats) triple consistent.
    public Snapshot? Current => _running == 1 ? _current : null;

    // Summary of the most recently finished job (completed/cancelled/failed), or
    // null if none has run since startup.
    public RunSummary? LastRun => _lastRun;
}
