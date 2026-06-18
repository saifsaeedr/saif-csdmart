using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Services;

// Sidecar JSON file that records pass-level completion markers for
// `dmart import`. Lets a crashed import resume from the last committed
// pass instead of replaying the whole 24M-entry tree.
//
// The five-pass architecture in ImportExportService.ImportFromEntriesAsync
// is already idempotent under preserveExisting=true (ON CONFLICT DO
// NOTHING on insert; per-space FastImportScope commits independently
// in the parallel path). What it lacks is a record of "I finished
// pass X for space Y". CheckpointStore writes a marker after each
// commit so the next `--resume` run skips the pass entirely instead
// of re-doing the bulk COPY just to land zero new rows.
//
// File layout:
//   {
//     "started_at": "2026-05-26T14:48:19Z",
//     "source_path": "/var/lib/dmart/spaces",
//     "passes_done": ["head"],          // head = users + spaces + roles + permissions
//     "tail_done":   ["applications", "products"]   // per-space tail markers
//   }
//
// Atomic writes via `.tmp` + rename — same pattern as PreflightService's
// JSON rewriter and the prototype regen-*.sh scripts.
//
// Scope today:
//   * Filesystem imports only. Zip imports don't get resume because the
//     natural sidecar location (next to the zip file) is operator-
//     unfriendly when the zip is on remote storage. Reaching the 24M
//     target via fs+--fast is the canonical path; zip is a smaller-
//     scale convenience.
//   * Tail-pass resume requires `--fast --fast-parallelism=N>1`. The
//     serial path doesn't have per-space transaction boundaries, so a
//     mid-run crash can leave partial state for the active space —
//     resume would re-do that space which is fine, but there's no
//     speed-up because the entire pass replays anyway.
public sealed class ImportCheckpointStore
{
    [JsonIgnore] private readonly string _path;
    [JsonIgnore] private readonly object _lock = new();

    [JsonPropertyName("started_at")]   public string StartedAt   { get; set; } = "";
    [JsonPropertyName("source_path")]  public string SourcePath  { get; set; } = "";
    [JsonPropertyName("passes_done")]  public List<string> PassesDone { get; set; } = new();
    [JsonPropertyName("tail_done")]    public List<string> TailDone   { get; set; } = new();

    // Parameterless ctor for JSON deserialization; never use directly.
    public ImportCheckpointStore() { _path = ""; }

    private ImportCheckpointStore(string path)
    {
        _path = path;
    }

    // Load an existing checkpoint or return a fresh one. Path-aware:
    // when no file exists the returned store is empty and writes will
    // create the file on first MarkXxxDone().
    public static ImportCheckpointStore LoadOrCreate(
        string path, string sourcePath, Microsoft.Extensions.Logging.ILogger? log = null)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(json, DmartJsonContext.Default.ImportCheckpointStore);
                if (loaded is not null)
                {
                    // The constructor used by JSON deserialization can't take
                    // _path (no [JsonConstructor] hook on the non-default
                    // ctor), so we patch it via reflection-free pattern: new
                    // wrapper that copies the lists into a path-aware store.
                    var store = new ImportCheckpointStore(path)
                    {
                        StartedAt = loaded.StartedAt,
                        SourcePath = loaded.SourcePath,
                        PassesDone = loaded.PassesDone,
                        TailDone = loaded.TailDone,
                    };
                    return store;
                }
            }
            catch (Exception ex)
            {
                // Corrupt checkpoint — treat as a fresh start. The operator
                // can delete it manually if they want a clean run; we don't
                // touch the source files on a parse failure. Surface it so a
                // multi-hour import silently restarting from scratch is
                // visible rather than a mystery.
                if (log is not null)
                    Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                        log, ex,
                        "import: corrupt checkpoint at {Path} — ignoring it and starting fresh; delete the file to silence this",
                        path);
            }
        }
        return new ImportCheckpointStore(path)
        {
            StartedAt = DateTimeOffset.UtcNow.ToString("o"),
            SourcePath = sourcePath,
        };
    }

    // ---- Read-side queries used by the import orchestrator ---------

    public bool IsHeadDone() => PassesDone.Contains("head");

    public bool IsTailDone(string spaceName) => TailDone.Contains(spaceName);

    // ---- Write-side markers ----------------------------------------

    public void MarkHeadDone()
    {
        lock (_lock)
        {
            if (!PassesDone.Contains("head")) PassesDone.Add("head");
            FlushUnsafe();
        }
    }

    public void MarkTailDone(string spaceName)
    {
        lock (_lock)
        {
            if (!TailDone.Contains(spaceName)) TailDone.Add(spaceName);
            FlushUnsafe();
        }
    }

    // Best-effort cleanup once the whole import completed successfully.
    // Operator may also delete this file manually if they want.
    public void Clear()
    {
        lock (_lock)
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }

    // ---- Internal helpers ------------------------------------------

    // Called with _lock held. Atomic via .tmp + rename so a crash
    // mid-write can't leave a half-written checkpoint that fails to
    // parse on next startup (which the LoadOrCreate path would treat
    // as "start over").
    private void FlushUnsafe()
    {
        if (string.IsNullOrEmpty(_path)) return;
        var json = JsonSerializer.Serialize(this, DmartJsonContext.Default.ImportCheckpointStore);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    // Default checkpoint file location for filesystem imports.
    public static string DefaultPathFor(string sourceFolder)
        => Path.Combine(sourceFolder, ".dmart-import-checkpoint.json");
}
