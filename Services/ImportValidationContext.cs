using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;

namespace Dmart.Services;

// Per-import state shared across all workers when validation is enabled.
// Holds:
//   * the set of known-good user shortnames (built during head pass)
//   * the in-memory UUID dedup set (cross-worker; populated as entries
//     are validated)
//   * the source-filesystem-backed schema cache (compiles each schema
//     file once, on demand)
//   * the issue sink for sidecar JSONL output
//
// All members are thread-safe for use under Parallel.ForEachAsync. Both
// the user set and the UUID set are ConcurrentDictionary-backed; the
// schema cache also uses ConcurrentDictionary internally.
//
// Created at the top of ImportFolderAsync when validation is enabled
// (default), disposed at the end of the import. The sink is the only
// disposable resource — it owns a background drain task.
public sealed class ImportValidationContext : IAsyncDisposable
{
    private static readonly EvaluationOptions EvalOptions = new() { OutputFormat = OutputFormat.List };

    private readonly ConcurrentDictionary<string, byte> _knownUsers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _seenUuids = new();
    // (space, schemaShortname) → compiled schema (or null = "lookup attempted, not found")
    private readonly ConcurrentDictionary<(string, string), JsonSchema?> _schemaCache = new();

    private readonly string _sourceRoot;
    private readonly ILogger _log;

    public ImportIssueSink Sink { get; }

    public ImportValidationContext(string sourceRoot, ImportIssueSink sink, ILogger log)
    {
        _sourceRoot = sourceRoot;
        Sink = sink;
        _log = log;
        // "dmart" is the sentinel owner — always considered valid even
        // when the source has no explicit user meta for it. Matches the
        // EnsureOwner fallback already in the import path.
        _knownUsers.TryAdd("dmart", 1);
    }

    // Called from the head pass as user metas are processed. Idempotent.
    public void RegisterKnownUser(string shortname)
    {
        if (!string.IsNullOrEmpty(shortname))
            _knownUsers.TryAdd(shortname, 1);
    }

    // ===================================================================
    // Owner check
    // ===================================================================

    // Returns true if the owner is known. False means the caller should
    // remap to "dmart" before insert and log an issue.
    public bool IsKnownOwner(string ownerShortname)
        => _knownUsers.ContainsKey(ownerShortname);

    // ===================================================================
    // UUID dedup
    // ===================================================================

    // Attempts to claim `uuid` for the given path. Returns true if this
    // was the first claim (caller may use uuid as-is). Returns false if
    // another path has already claimed this uuid — caller should
    // regenerate via Guid.NewGuid() and log a uuid-regenerated issue.
    public bool TryClaimUuid(Guid uuid, string path)
        => _seenUuids.TryAdd(uuid, path);

    public string? GetUuidOwner(Guid uuid)
        => _seenUuids.TryGetValue(uuid, out var p) ? p : null;

    // Used after a regeneration so the new uuid is also tracked (covers
    // the unlikely case of two regenerations colliding).
    public void RegisterClaimedUuid(Guid uuid, string path)
        => _seenUuids[uuid] = path;

    // ===================================================================
    // Schema validation
    // ===================================================================

    // Returns null on success (or schema not found — lenient, matches
    // SchemaValidator's runtime behaviour). Returns a list of error
    // strings on failure.
    public async Task<List<string>?> ValidateBodyAsync(
        string spaceName, string schemaShortname, JsonElement body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(schemaShortname)) return null;
        var schema = await GetSchemaAsync(spaceName, schemaShortname, ct);
        if (schema is null) return null;

        var result = schema.Evaluate(body, EvalOptions);
        if (result.IsValid) return null;

        var errors = new List<string>();
        CollectErrors(result, errors);
        return errors.Count > 0 ? errors : null;
    }

    private async Task<JsonSchema?> GetSchemaAsync(string space, string shortname, CancellationToken ct)
    {
        var key = (space, shortname);
        if (_schemaCache.TryGetValue(key, out var cached)) return cached;

        // Try canonical schema subpath layouts. Matches the locations
        // dmart Python writes schemas to and matches PreflightService's
        // SchemaScanner. First file that compiles wins.
        foreach (var sub in new[] { ".dm/schema", ".dm/schemas", "schema/.dm", "schemas/.dm" })
        {
            var candidate = Path.Combine(_sourceRoot, space, sub, shortname, "meta.schema.json");
            if (!File.Exists(candidate)) continue;
            try
            {
                var json = await File.ReadAllTextAsync(candidate, ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("payload", out var payload)) continue;
                if (!payload.TryGetProperty("body", out var bodyEl)) continue;
                // Schema body can be either:
                //   (a) inline JSON Schema object — use as-is
                //   (b) a filename string pointing at a sibling JSON file
                JsonSchema? compiled = null;
                if (bodyEl.ValueKind == JsonValueKind.Object)
                {
                    compiled = JsonSchema.FromText(bodyEl.GetRawText());
                }
                else if (bodyEl.ValueKind == JsonValueKind.String)
                {
                    var bodyFilename = bodyEl.GetString();
                    if (!string.IsNullOrEmpty(bodyFilename))
                    {
                        var schemaBodyPath = Path.Combine(
                            Path.GetDirectoryName(candidate)!, "..", "..", "..", bodyFilename);
                        // Fall back to the schemas folder sibling — typical layout.
                        if (!File.Exists(schemaBodyPath))
                            schemaBodyPath = Path.Combine(_sourceRoot, space, "schema", bodyFilename);
                        if (File.Exists(schemaBodyPath))
                        {
                            var schemaJson = await File.ReadAllTextAsync(schemaBodyPath, ct);
                            compiled = JsonSchema.FromText(schemaJson);
                        }
                    }
                }
                if (compiled is not null)
                {
                    _schemaCache[key] = compiled;
                    return compiled;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "import-validate: failed to compile schema {Space}/{Shortname}", space, shortname);
                _schemaCache[key] = null;
                return null;
            }
        }
        _schemaCache[key] = null;
        return null;
    }

    private static void CollectErrors(EvaluationResults r, List<string> errors)
    {
        if (r.Errors is not null)
            foreach (var (key, msg) in r.Errors)
                errors.Add($"{r.InstanceLocation}: {key}: {msg}");
        if (r.Details is { Count: > 0 })
            foreach (var d in r.Details) CollectErrors(d, errors);
    }

    // ===================================================================
    // Lifecycle
    // ===================================================================

    public async ValueTask DisposeAsync()
    {
        await Sink.DisposeAsync();
    }
}
