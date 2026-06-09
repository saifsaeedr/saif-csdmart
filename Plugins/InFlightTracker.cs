using System.Collections.Concurrent;

namespace Dmart.Plugins;

// Tracks fire-and-forget concurrent plugin-hook tasks so graceful shutdown can
// wait (bounded) for them to finish instead of tearing them down mid-write.
//
// Concurrent after-hooks run detached (a slow hook must not delay the HTTP
// response), but on SIGTERM the host should drain them. Hooks observe
// ShutdownToken, so a drain that times out also signals cancellation to any
// straggler that bothered to honor it.
internal sealed class InFlightTracker
{
    private readonly ConcurrentDictionary<Task, byte> _inflight = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    // Passed to each concurrent hook so a timed-out drain can ask stragglers
    // to stop. Hooks are free to ignore it (fire-and-forget), but well-behaved
    // ones unwind promptly.
    public CancellationToken ShutdownToken => _shutdownCts.Token;

    public void Track(Task task)
    {
        _inflight[task] = 0;
        // Remove on completion. ContinueWith receives the task reference, so
        // there's no add/remove race even if the task finishes before this
        // line runs (the continuation then fires immediately).
        task.ContinueWith(
            static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
            _inflight, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Wait up to `timeout` for all tracked tasks to finish. Returns true if they
    // all completed, false if the timeout was hit. Either way the shutdown token
    // is cancelled afterward. Short-circuits when nothing is in flight — this
    // runs on every host teardown, so the empty case must be instant.
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var snapshot = _inflight.Keys.ToArray();
        if (snapshot.Length == 0)
        {
            _shutdownCts.Cancel();
            return true;
        }

        var all = Task.WhenAll(snapshot);
        var finished = await Task.WhenAny(all, Task.Delay(timeout, ct)).ConfigureAwait(false) == all;
        _shutdownCts.Cancel();
        return finished;
    }
}
