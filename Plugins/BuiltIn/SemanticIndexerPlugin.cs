using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Plugins.BuiltIn;

// Maintains `entries.embedding` vectors for semantic search.
//
// Runs on every after-create / after-update event, fetches the entry, and
// delegates to `SemanticIndexerService.ReindexEntryAsync`. Opt-in at three
// levels:
//   1. pgvector extension installed + `entries.embedding` column present.
//   2. EmbeddingApiUrl configured in DmartSettings.
//   3. The triggering space has "semantic_indexer" in its active_plugins.
//
// Failures are logged and swallowed — indexing never blocks a write.
public sealed class SemanticIndexerPlugin(
    SemanticIndexerService indexer,
    EntryRepository entries,
    EmbeddingProvider embeddings,
    ILogger<SemanticIndexerPlugin> log) : IHookPlugin
{
    public string Shortname => "semantic_indexer";

    public async Task HookAsync(Event e, CancellationToken ct = default)
    {
        if (e.ActionType != ActionType.Create && e.ActionType != ActionType.Update)
            return;
        if (string.IsNullOrEmpty(e.Shortname) || e.ResourceType is null)
            return;
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

        await indexer.ReindexEntryAsync(entry, ct);
    }
}
