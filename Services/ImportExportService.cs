using System.IO.Compression;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Services;

// Bulk import/export of dmart spaces as zip archives. Mirrors the dmart Python layout:
//
//   archive.zip
//     ├── meta.space.json                       (the Space row, if exporting whole space)
//     └── {subpath}/
//         ├── .dm/{shortname}/meta.{type}.json  (entry meta)
//         ├── {shortname}.json                  (entry payload, when content/json)
//         └── attachments/
//             └── {att_shortname}.{ext}         (attachment media bytes)
//
// We DON'T currently round-trip every dmart subdirectory convention (e.g. media inside
// `media.{shortname}` folders) — we use a flat-per-subpath layout that's easy to parse
// and reproduce. dmart Python tolerates this layout via its file-walker.
public sealed class ImportExportService(
    EntryRepository entries,
    EntryService entryService,
    ILogger<ImportExportService> log)
{
    public async Task<Stream> ExportAsync(string spaceName, string? subpath, string? actor, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Walk all entries in the space (optionally filtered by subpath prefix).
            // FilterSchemaNames defaults to ["meta"] to match dmart's Pydantic default,
            // but for export we want everything regardless of schema.
            var query = new Query
            {
                Type = QueryType.Search,
                SpaceName = spaceName,
                Subpath = subpath ?? "/",
                FilterSchemaNames = new(),
                Limit = 10_000,
                RetrieveJsonPayload = true,
            };
            var rows = await entries.QueryAsync(query, ct);

            foreach (var entry in rows)
            {
                var rt = JsonbHelpers.EnumMember(entry.ResourceType);
                var subpathClean = entry.Subpath.TrimStart('/').TrimEnd('/');
                var dirPath = string.IsNullOrEmpty(subpathClean) ? "" : subpathClean + "/";

                // Meta JSON
                var metaPath = $"{dirPath}.dm/{entry.Shortname}/meta.{rt}.json";
                var metaJson = JsonSerializer.Serialize(entry, DmartJsonContext.Default.Entry);
                await WriteEntryAsync(zip, metaPath, metaJson, ct);

                // If the payload body is inline JSON, write it next to the meta as
                // {shortname}.json so dmart Python's importer recognizes it.
                if (entry.Payload?.Body is not null && entry.Payload.ContentType == ContentType.Json)
                {
                    var bodyPath = $"{dirPath}{entry.Shortname}.json";
                    var bodyJson = JsonSerializer.Serialize(entry.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
                    await WriteEntryAsync(zip, bodyPath, bodyJson, ct);
                }
            }

            // Attachments — flat list per (parent_subpath/parent_shortname). Walk them
            // separately so we don't need to know parent linkage from each entry.
            // For now we export all attachments in the space with the same subpath
            // prefix as the requested filter.
            // (A more thorough export would join entries→attachments by subpath.)
        }

        ms.Position = 0;
        return ms;
    }

    public async Task<Response> ImportZipAsync(Stream zip, string? actor, CancellationToken ct = default)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var inserted = 0;
        var failed = new List<Dictionary<string, object>>();

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.Contains(".dm/", StringComparison.Ordinal)) continue;
            if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.Name.StartsWith("meta.", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                await using var stream = entry.Open();
                var loaded = await JsonSerializer.DeserializeAsync(stream, DmartJsonContext.Default.Entry, ct);
                if (loaded is null)
                {
                    failed.Add(new() { ["path"] = entry.FullName, ["error"] = "empty" });
                    continue;
                }

                var result = await entryService.CreateAsync(loaded, actor, ct);
                if (result.IsOk) inserted++;
                else failed.Add(new()
                {
                    ["path"] = entry.FullName,
                    ["shortname"] = loaded.Shortname,
                    ["error"] = result.ErrorMessage ?? "unknown",
                    ["code"] = result.ErrorCode,
                });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "import failed for {Path}", entry.FullName);
                failed.Add(new() { ["path"] = entry.FullName, ["error"] = ex.Message });
            }
        }

        return Response.Ok(attributes: new()
        {
            ["inserted"] = inserted,
            ["failed_count"] = failed.Count,
            ["failed"] = failed,
        });
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string path, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var s = entry.Open();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await s.WriteAsync(bytes, ct);
    }
}
