using System.Collections.Concurrent;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Json.Schema;

namespace Dmart.Services;

// Mirrors dmart Python's payload-vs-schema validation. dmart stores JSON Schema
// documents as `schema`-typed entries in each space (typically under /schema or
// /schemas subpath). Other entries reference them via payload.schema_shortname,
// and the payload.body is validated against that schema before being stored.
//
// Compiled schemas are cached in-process keyed by (space, shortname). Cache is
// invalidated lazily — call ClearCache after upserting a schema entry.
public sealed class SchemaValidator(EntryRepository entries, ILogger<SchemaValidator> log)
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
    };

    private readonly ConcurrentDictionary<(string Space, string Shortname), JsonSchema?> _cache = new();

    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Validates a JSON payload body against a schema named in the same space.
    /// Returns null on success; the list of error messages on failure.
    /// Returns null when the schema can't be found (treats missing schemas as
    /// non-fatal — matches dmart's lenient behavior on first-write of a schema-
    /// less payload).
    /// </summary>
    public async Task<List<string>?> ValidateAsync(string spaceName, string schemaShortname, JsonElement body, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(schemaShortname)) return null;
        var schema = await GetCompiledAsync(spaceName, schemaShortname, ct);
        if (schema is null) return null;   // schema not found — pass through

        var result = schema.Evaluate(body, Options);
        if (result.IsValid) return null;

        var errors = new List<string>();
        Collect(result, errors);
        return errors.Count > 0 ? errors : null;
    }

    private static void Collect(EvaluationResults r, List<string> errors)
    {
        if (r.Errors is not null)
            foreach (var (key, msg) in r.Errors)
                errors.Add($"{r.InstanceLocation}: {key}: {msg}");
        if (r.Details is { Count: > 0 })
            foreach (var d in r.Details) Collect(d, errors);
    }

    private async Task<JsonSchema?> GetCompiledAsync(string spaceName, string shortname, CancellationToken ct)
    {
        var key = (spaceName, shortname);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        // dmart stores schemas as entries with resource_type='schema'. The actual
        // JSON Schema document lives in payload.body (jsonb).
        // We try a few canonical subpaths since dmart projects vary; the first hit wins.
        Entry? schemaEntry = null;
        foreach (var sub in new[] { "/schema", "/schemas", "/" })
        {
            schemaEntry = await entries.GetAsync(spaceName, sub, shortname, ResourceType.Schema, ct);
            if (schemaEntry is not null) break;
        }

        if (schemaEntry?.Payload?.Body is null)
        {
            // Don't cache misses — callers supply schema_shortname intentionally,
            // and caching null would pin the "not found" result until process
            // restart even after the schema is created externally (e.g. via
            // dmart Python or direct SQL). The cost is a repeat DB lookup per
            // payload that references a missing schema, which should be rare.
            log.LogDebug("schema {Space}/{Shortname} not found", spaceName, shortname);
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(schemaEntry.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
            var schema = JsonSchema.FromText(json);
            _cache[key] = schema;
            return schema;
        }
        catch (Exception ex)
        {
            // Compile failure is (probably) a programming error in the schema itself;
            // still don't cache it — the author may fix and re-upsert.
            log.LogWarning(ex, "failed to compile schema {Space}/{Shortname}", spaceName, shortname);
            return null;
        }
    }
}
