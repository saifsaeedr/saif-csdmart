using System.Collections.Concurrent;
using System.Text.Json;
using Dmart.Models.Json;

namespace Dmart.Services;

// Thread-safe JSONL writer for import-time validation issues. Multiple
// per-space workers can call Add concurrently from inside Parallel.ForEachAsync;
// a single background drain task serialises them to disk. The drain runs on
// a dedicated Task so worker threads never block on file I/O for the sidecar.
//
// Lifetime: created at the start of an import that has validation enabled,
// disposed when the import completes (success or failure). DisposeAsync
// flushes the remaining queue and waits for the drain task to finish.
public sealed class ImportIssueSink : IAsyncDisposable
{
    private readonly BlockingCollection<ImportIssue> _queue = new(new ConcurrentQueue<ImportIssue>());
    private readonly Task _drainTask;
    private readonly string _path;
    private int _count;

    public string Path => _path;
    public int Count => Volatile.Read(ref _count);

    public ImportIssueSink(string path)
    {
        _path = path;
        // Ensure the parent directory exists. Sidecar files can be placed
        // anywhere the operator specifies; if the dir is missing it's
        // almost certainly an operator typo (don't silently swallow it).
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _drainTask = Task.Run(DrainLoopAsync);
    }

    public void Add(ImportIssue issue)
    {
        Interlocked.Increment(ref _count);
        // CompleteAdding may have been called while we were entering; in
        // that case TryAdd returns false and we drop the issue rather than
        // throw — the import is already shutting down.
        _queue.TryAdd(issue);
    }

    private async Task DrainLoopAsync()
    {
        await using var writer = new StreamWriter(_path, append: false);
        foreach (var issue in _queue.GetConsumingEnumerable())
        {
            try
            {
                var obj = new Dictionary<string, object?>
                {
                    ["path"] = issue.Path,
                    ["kind"] = issue.Kind,
                    ["action"] = issue.Action,
                };
                if (issue.Details is not null)
                    foreach (var (k, v) in issue.Details) obj[k] = v;

                var json = JsonSerializer.Serialize(obj, DmartJsonContext.Default.DictionaryStringObject);
                await writer.WriteLineAsync(json);
            }
            catch
            {
                // Sidecar write failure is not fatal to the import. The
                // operator will see the resulting truncated sidecar and
                // know to investigate; the import itself proceeds.
            }
        }
        await writer.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.CompleteAdding();
        try { await _drainTask; } catch { /* drain task already swallows */ }
        _queue.Dispose();
    }
}
