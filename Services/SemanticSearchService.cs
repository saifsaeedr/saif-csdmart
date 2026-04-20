using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Npgsql;

namespace Dmart.Services;

// Runs cosine-similarity queries against the pgvector-backed
// `entries.embedding` column. Returns compact match records — space,
// subpath, shortname, resource_type, similarity — leaving it to the caller
// to `dmart_read` each hit for full content. That keeps response size
// bounded regardless of how many matches come back.
//
// Permission filtering: for each hit we check
// `PermissionService.CanReadAsync`, so the caller only sees entries they
// could see through `dmart_read` / `dmart_query`. Hits the user can't see
// are silently dropped from the result set — we over-fetch from SQL to
// keep the final count close to `limit`.
public sealed class SemanticSearchService(
    Db db,
    PermissionService perms,
    EmbeddingProvider embeddings,
    ILogger<SemanticSearchService> log)
{
    // pgvector returns distance (0 = identical) for `<=>`. Similarity is
    // conventionally 1 - distance. We expose similarity since it's what
    // LLMs and humans both find natural ("higher is better").
    private const int OverFetchMultiplier = 3;
    private const int AbsoluteMaxLimit = 100;

    public async Task<Response> SearchAsync(
        string query, string? spaceName, string? subpath,
        IReadOnlyList<ResourceType>? resourceTypes, int limit,
        string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Response.Fail(InternalErrorCode.MISSING_DATA,
                "semantic_search requires a non-empty `query`", ErrorTypes.Request);

        // Specific diagnostic per missing piece so operators don't have to
        // guess which side of the pairing isn't wired yet.
        if (!embeddings.IsProviderConfigured)
            return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                "semantic search not configured — set EMBEDDING_API_URL (and EMBEDDING_API_KEY) in config.env",
                ErrorTypes.Request);
        if (!await embeddings.IsPgVectorAvailableAsync(ct))
            return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                "semantic search not available — pgvector extension is not installed in the dmart database. " +
                "Ask a DBA to run: CREATE EXTENSION vector",
                ErrorTypes.Request);

        var vec = await embeddings.EmbedAsync(query, ct);
        if (vec is null)
            return Response.Fail(InternalErrorCode.SOMETHING_WRONG,
                "embedding provider returned no vector — check logs", ErrorTypes.Internal);

        var clampedLimit = Math.Min(Math.Max(1, limit), AbsoluteMaxLimit);
        var overFetch = clampedLimit * OverFetchMultiplier;

        var rows = await QueryPgVectorAsync(vec, spaceName, subpath, resourceTypes, overFetch, ct);

        // Permission filter. Build Records only for entries the caller can see.
        var kept = new List<Record>(clampedLimit);
        foreach (var row in rows)
        {
            var locator = new Locator(row.ResourceType, row.SpaceName, row.Subpath, row.Shortname);
            if (!await perms.CanReadAsync(actor, locator, ct)) continue;
            kept.Add(new Record
            {
                ResourceType = row.ResourceType,
                Shortname = row.Shortname,
                Subpath = row.Subpath,
                Uuid = row.Uuid,
                Attributes = new()
                {
                    ["space_name"] = row.SpaceName,
                    ["similarity"] = row.Similarity,
                    ["uri"] = $"dmart://{row.SpaceName}{(row.Subpath == "/" ? "" : row.Subpath)}/{row.Shortname}",
                },
            });
            if (kept.Count >= clampedLimit) break;
        }

        return Response.Ok(kept, new Dictionary<string, object>
        {
            ["returned"] = kept.Count,
            ["matched"] = rows.Count,
        });
    }

    private async Task<List<Hit>> QueryPgVectorAsync(
        float[] vec, string? spaceName, string? subpath,
        IReadOnlyList<ResourceType>? resourceTypes, int limit,
        CancellationToken ct)
    {
        var sql = """
            SELECT uuid, shortname, space_name, subpath, resource_type,
                   (embedding <=> $1::vector) AS distance
              FROM entries
             WHERE embedding IS NOT NULL
            """;
        var parameters = new List<NpgsqlParameter>
        {
            new() { Value = EmbeddingProvider.FormatVectorLiteral(vec) },
        };

        if (!string.IsNullOrEmpty(spaceName))
        {
            sql += $" AND space_name = ${parameters.Count + 1}";
            parameters.Add(new() { Value = spaceName });
        }
        if (!string.IsNullOrEmpty(subpath))
        {
            sql += $" AND subpath LIKE ${parameters.Count + 1}";
            parameters.Add(new() { Value = subpath == "/" ? "/%" : subpath.TrimEnd('/') + "%" });
        }
        if (resourceTypes is { Count: > 0 })
        {
            var placeholders = string.Join(",",
                resourceTypes.Select((_, i) => $"${parameters.Count + i + 1}"));
            sql += $" AND resource_type IN ({placeholders})";
            foreach (var rt in resourceTypes)
                parameters.Add(new() { Value = JsonbHelpers.EnumMember(rt) });
        }

        sql += $" ORDER BY embedding <=> $1::vector LIMIT ${parameters.Count + 1}";
        parameters.Add(new() { Value = limit });

        var hits = new List<Hit>(limit);
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var p in parameters) cmd.Parameters.Add(p);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rtStr = reader.GetString(4);
                if (!TryParseResourceType(rtStr, out var rt)) continue;
                var distance = reader.GetDouble(5);
                hits.Add(new Hit(
                    Uuid: reader.GetGuid(0).ToString(),
                    Shortname: reader.GetString(1),
                    SpaceName: reader.GetString(2),
                    Subpath: reader.GetString(3),
                    ResourceType: rt,
                    Similarity: Math.Max(0.0, 1.0 - distance)));
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "semantic_search pgvector query failed");
        }
        return hits;
    }

    private static bool TryParseResourceType(string s, out ResourceType rt)
    {
        // DB stores the EnumMember string ("content","folder",...); fall back
        // to the C# name for safety (same pattern the main EntryRepository
        // uses via JsonbHelpers.EnumMember's inverse).
        foreach (var candidate in Enum.GetValues<ResourceType>())
        {
            if (string.Equals(JsonbHelpers.EnumMember(candidate), s, StringComparison.Ordinal))
            { rt = candidate; return true; }
        }
        if (Enum.TryParse(s, ignoreCase: true, out rt)) return true;
        rt = default;
        return false;
    }

    private sealed record Hit(
        string Uuid, string Shortname, string SpaceName, string Subpath,
        ResourceType ResourceType, double Similarity);
}
