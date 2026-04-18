using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Npgsql;

namespace Dmart.Services;

// Owns single-entry and bulk reindex loops for semantic embeddings. Split
// out of the plugin so both the after-hook (on each create/update) and the
// admin reindex endpoint (backfill / after a model swap) share one
// implementation.
//
// Two public surfaces:
//   * ReindexEntryAsync(entry) — the after-hook path. Called inline by
//     SemanticIndexerPlugin for every eligible event.
//   * ReindexAllAsync(space?, onlyMissing, ct) — bulk. Walks entries in
//     pages via EntryRepository.QueryAsync, embeds any that match the
//     filter, tracks per-category counts. Swallows individual-entry
//     failures so one bad row doesn't abort the whole sweep.
public sealed class SemanticIndexerService(
    EmbeddingProvider embeddings,
    EntryRepository entries,
    SpaceRepository spaces,
    Db db,
    ILogger<SemanticIndexerService> log)
{
    // Page size for the bulk walk. Small enough that one page fits in
    // memory easily; large enough that we're not round-tripping per row.
    private const int PageSize = 100;

    // Embed one in-memory entry and write the vector. No-op when disabled
    // or when the entry has nothing to embed. Never throws — logs and
    // returns false instead.
    public async Task<bool> ReindexEntryAsync(Entry entry, CancellationToken ct = default)
    {
        if (!await embeddings.IsEnabledAsync(ct)) return false;
        var text = BuildEmbeddableText(entry);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var vec = await embeddings.EmbedAsync(text, ct);
        if (vec is null) return false;

        try
        {
            await embeddings.UpdateEntryEmbeddingAsync(entry.Uuid, vec, ct);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "semantic_indexer: update failed for {Uuid}", entry.Uuid);
            return false;
        }
    }

    // Bulk reindex. When spaceName is null, walks every space with
    // "semantic_indexer" in its active_plugins — matches the per-space
    // opt-in convention. When spaceName is set, walks only that one and
    // skips the active_plugins check (admin-forced).
    public async Task<ReindexStats> ReindexAllAsync(
        string? spaceName, bool onlyMissing, int? maxPerSpace,
        CancellationToken ct = default)
    {
        var stats = new ReindexStats();
        if (!await embeddings.IsEnabledAsync(ct))
        {
            stats.Error = embeddings.IsProviderConfigured
                ? "pgvector extension not installed"
                : "EMBEDDING_API_URL not configured";
            return stats;
        }

        var targetSpaces = await ResolveTargetSpacesAsync(spaceName, ct);
        foreach (var space in targetSpaces)
        {
            stats.Spaces++;
            await ReindexOneSpaceAsync(space, onlyMissing, maxPerSpace, stats, ct);
            if (ct.IsCancellationRequested) break;
        }
        return stats;
    }

    private async Task<IReadOnlyList<string>> ResolveTargetSpacesAsync(
        string? explicitSpace, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(explicitSpace)) return [explicitSpace];

        var all = await spaces.ListAsync(ct);
        return all
            .Where(s => s.ActivePlugins is { Count: > 0 } &&
                        s.ActivePlugins.Contains("semantic_indexer", StringComparer.Ordinal))
            .Select(s => s.Shortname)
            .ToList();
    }

    private async Task ReindexOneSpaceAsync(
        string spaceName, bool onlyMissing, int? maxEntries,
        ReindexStats stats, CancellationToken ct)
    {
        var offset = 0;
        var processedInSpace = 0;
        while (true)
        {
            if (ct.IsCancellationRequested) return;

            var q = new Query
            {
                Type = QueryType.Search,
                SpaceName = spaceName,
                Subpath = "/",
                Limit = PageSize,
                Offset = offset,
                RetrieveJsonPayload = true,
            };
            var page = await entries.QueryAsync(q, ct);
            if (page.Count == 0) return;

            foreach (var entry in page)
            {
                stats.Scanned++;

                if (Api.Managed.ResourceWithPayloadHandler.IsAttachmentResourceType(entry.ResourceType))
                {
                    stats.Skipped++; continue;
                }

                if (onlyMissing && await HasEmbeddingAsync(entry.Uuid, ct))
                {
                    stats.Skipped++; continue;
                }

                var ok = await ReindexEntryAsync(entry, ct);
                if (ok) stats.Embedded++;
                else    stats.Failed++;

                processedInSpace++;
                if (maxEntries is int cap && processedInSpace >= cap) return;
            }

            offset += page.Count;
            if (page.Count < PageSize) return;  // last page
        }
    }

    // Quick existence check for the already-embedded test. Keeps the
    // main page query lean by not joining on embedding.
    private async Task<bool> HasEmbeddingAsync(string entryUuid, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT embedding IS NOT NULL FROM entries WHERE uuid = $1", conn);
        cmd.Parameters.Add(new() { Value = Guid.Parse(entryUuid) });
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is bool b && b;
    }

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
            Add(JsonSerializer.Serialize(body, DmartJsonContext.Default.JsonElement));
        }
        return sb.ToString();
    }
}

public sealed class ReindexStats
{
    public int Spaces { get; set; }
    public int Scanned { get; set; }
    public int Embedded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string? Error { get; set; }
}
