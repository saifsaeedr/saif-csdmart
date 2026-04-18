using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Plugins.BuiltIn;

// Maintains `entries.embedding` vectors for semantic search.
//
// Runs on every after-create / after-update event, fetches the entry, builds
// a text summary, calls the configured embedding provider, and writes the
// vector back. Completely opt-in — all three conditions must hold:
//   1. pgvector extension installed + `entries.embedding` column present.
//   2. EmbeddingApiUrl configured in DmartSettings.
//   3. The triggering space has "semantic_indexer" in its active_plugins.
//
// Failures (timeout, HTTP error, missing provider, etc.) are logged at
// Warning and swallowed — indexing never blocks a write. Plugin runs as an
// AFTER hook so the entry is already persisted by the time we get here;
// the embedding update is a fire-and-forget UPDATE on a single row.
public sealed class SemanticIndexerPlugin(
    EmbeddingProvider embeddings,
    EntryRepository entries,
    ILogger<SemanticIndexerPlugin> log) : IHookPlugin
{
    public string Shortname => "semantic_indexer";

    public async Task HookAsync(Event e, CancellationToken ct = default)
    {
        // Only react to create/update — moves/deletes/locks are noise.
        if (e.ActionType != ActionType.Create && e.ActionType != ActionType.Update)
            return;
        // Need a shortname + resource type to look up the entry.
        if (string.IsNullOrEmpty(e.Shortname) || e.ResourceType is null)
            return;
        // Skip attachment-flavor types — their bytes live in `attachments.media`,
        // not in `entries.embedding`. If we ever want to index attachments too,
        // it'll be a separate column/plugin.
        if (Api.Managed.ResourceWithPayloadHandler.IsAttachmentResourceType(e.ResourceType.Value))
            return;

        if (!await embeddings.IsEnabledAsync(ct)) return;

        Entry? entry;
        try
        {
            entry = await entries.GetAsync(
                e.SpaceName, e.Subpath, e.Shortname, e.ResourceType.Value, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "semantic_indexer: failed to reload entry {Space}/{Subpath}/{Shortname}",
                e.SpaceName, e.Subpath, e.Shortname);
            return;
        }
        if (entry is null) return;

        var text = BuildEmbeddableText(entry);
        if (string.IsNullOrWhiteSpace(text)) return;

        var vec = await embeddings.EmbedAsync(text, ct);
        if (vec is null) return;

        try
        {
            await embeddings.UpdateEntryEmbeddingAsync(entry.Uuid, vec, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "semantic_indexer: update failed for {Uuid}", entry.Uuid);
        }
    }

    // Concatenates the entry fields most likely to carry semantics:
    // shortname, displayname, description, tags, and the payload body (if
    // present, JSON-stringified). Cap is applied on the provider side via
    // EmbeddingProvider.MaxEmbedChars so we don't hit token limits.
    internal static string BuildEmbeddableText(Entry entry)
    {
        var sb = new StringBuilder();
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s);
        }

        Add(entry.Shortname);
        Add(entry.Displayname?.En);
        Add(entry.Displayname?.Ar);
        Add(entry.Description?.En);
        Add(entry.Description?.Ar);
        if (entry.Tags is { Count: > 0 })
            Add(string.Join(", ", entry.Tags));
        if (entry.Payload?.Body is JsonElement body &&
            body.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            // Inline JSON payload — stringify so the embedding captures the
            // content. Source-gen serialization keeps it AOT-safe.
            Add(JsonSerializer.Serialize(body, DmartJsonContext.Default.JsonElement));
        }
        return sb.ToString();
    }
}
