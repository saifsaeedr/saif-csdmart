namespace Dmart.Tests.Infrastructure;

// Polling helper for integration tests. Replaces ad-hoc `await Task.Delay(ms)`
// where the test is actually waiting for an observable condition (a DB row to
// appear, a log line to flush, a lock to expire). Fixed sleeps either flake on
// slow CI or waste wall time on fast CI; polling picks the shortest wait that
// actually sees the condition.
//
// Not a replacement for genuine "wait N seconds without observing anything"
// waits — e.g. session TTL tests that must avoid touching the session still
// need a bare Task.Delay.
public static class WaitFor
{
    public static async Task<bool> UntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        TimeSpan? interval = null,
        CancellationToken ct = default)
    {
        var step = interval ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await predicate()) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(step, ct);
        }
    }
}
