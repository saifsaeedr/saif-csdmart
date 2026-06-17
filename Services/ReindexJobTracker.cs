namespace Dmart.Services;

// Singleton gate that ensures at most one reindex job runs at a time and
// exposes live progress for status reads.
//
// Thread-safety model:
//   * TryStart / Finish use Interlocked.CompareExchange on _running — two
//     concurrent HTTP requests can't both win the slot.
//   * The ReindexStats reference is written once inside TryStart (before the
//     background task starts) and read by status queries. Individual stat
//     counters are incremented by the single background thread, so the int
//     reads from HTTP handler threads may be slightly stale — that's
//     acceptable for a progress display.
public sealed class ReindexJobTracker
{
    private volatile int _running;           // 0 = idle, 1 = running
    private volatile ReindexStats? _stats;
    private string? _space;
    private DateTimeOffset _startedAt;

    // Atomically acquire the slot and set up a fresh stats object for the job.
    // Returns the live ReindexStats to pass into ReindexAllAsync, or null when
    // another job is already in progress.
    public ReindexStats? TryStart(string? space)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return null;
        _space = space ?? "__all__";
        _startedAt = DateTimeOffset.UtcNow;
        _stats = new ReindexStats();
        return _stats;
    }

    // Release the slot. Call from finally so failures always clear the flag.
    public void Finish()
    {
        Interlocked.Exchange(ref _running, 0);
    }

    public bool IsRunning => _running == 1;

    public (ReindexStats Stats, string Space, DateTimeOffset StartedAt)? GetRunningStatus()
    {
        if (_running == 0 || _stats is null) return null;
        return (_stats, _space ?? "__all__", _startedAt);
    }
}
