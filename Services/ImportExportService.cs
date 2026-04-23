using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Bulk import / export of dmart spaces as zip archives.
// Round-trips byte-for-byte with dmart Python's import/export so a Python
// export can be imported by csdmart and vice versa.
//
// Zip layout (mirrors Python's spaces_folder filesystem):
//
//   {space}/
//     .dm/meta.space.json                         Space row
//     {subpath}/.dm/{shortname}/
//       meta.{resource_type}.json                 Non-folder entry meta
//       history.jsonl                             Per-entry history (optional)
//       attachments.{rt}/
//         meta.{att_shortname}.json               Attachment meta
//         {att_body_filename}                     Body file (image / binary)
//         {att_shortname}.json                    JSON body (when payload is json)
//     {subpath}/{shortname}.json                  Externalized JSON payload body
//     {subpath}/{shortname}.html                  Externalized HTML body
//     {subpath}/{folder_shortname}/.dm/
//       meta.folder.json                          Folder meta (inside the folder itself)
//     {subpath}/{folder_shortname}.json           Folder payload body (optional)
//   management/users/.dm/{sn}/meta.user.json      Only when exporting the management space
//   management/roles/.dm/{sn}/meta.role.json
//   management/permissions/.dm/{sn}/meta.permission.json
//
// Export honors the actor's row-level ACL for Entries (the per-resource
// repos don't have the same policy hook today — users / roles / permissions
// surface unfiltered when the actor can query the management space at
// all). Import runs every write as the acting user.
//
// Legacy C# flat layout (no `{space}/` root) is NOT accepted — the import
// side hard-fails with a structured error. See the `legacy_layout` check
// below.
public sealed class ImportExportService(
    EntryRepository entries,
    AttachmentRepository attachments,
    UserRepository users,
    AccessRepository access,
    SpaceRepository spaces,
    HistoryRepository histories,
    PermissionService perms,
    IOptions<DmartSettings> settingsOpt,
    ILogger<ImportExportService> log)
{
    private const int QueryLimit = 100_000;
    private string MgmtSpace => settingsOpt.Value.ManagementSpace;

    // ========================================================================
    // EXPORT
    // ========================================================================

    // Back-compat shim for older in-tree callers. External scripts calling
    // this method continue to work; /managed/export now funnels through the
    // Query overload below.
    public Task<Stream> ExportAsync(string spaceName, string? subpath, string? actor, CancellationToken ct = default)
        => ExportAsync(new Query
        {
            Type = QueryType.Search,
            SpaceName = spaceName,
            Subpath = subpath ?? "/",
            FilterSchemaNames = new(),
            Limit = QueryLimit,
            RetrieveJsonPayload = true,
        }, actor, ct);

    public async Task<Stream> ExportAsync(Query clientQuery, string? actor, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await ExportInternalAsync(zip, clientQuery, actor, ct);
        }
        ms.Position = 0;
        return ms;
    }

    private async Task ExportInternalAsync(ZipArchive zip, Query clientQuery, string? actor, CancellationToken ct)
    {
        var spaceName = clientQuery.SpaceName;
        var subpath = string.IsNullOrEmpty(clientQuery.Subpath) ? "/" : clientQuery.Subpath;

        // Row-level ACL — same gate /managed/query applies for Entry reads.
        // An unauthenticated caller skips this step and gets unfiltered rows.
        List<string>? policies = null;
        if (actor is not null)
        {
            policies = await perms.BuildUserQueryPoliciesAsync(actor, spaceName, subpath, ct);
            if (policies.Count == 0) return; // empty archive
        }

        // 1. Space meta under `{space}/.dm/meta.space.json`.
        var space = await spaces.GetAsync(spaceName, ct);
        if (space is not null)
            await WriteSpaceMetaAsync(zip, space, ct);

        // 2. Management-only extras — users / roles / permissions. Python
        //    selectively writes these based on the requested subpath:
        //      subpath == ""           → all three
        //      subpath == "users"      → users only
        //      subpath == "roles"      → roles only
        //      subpath == "permissions"→ permissions only
        if (string.Equals(spaceName, MgmtSpace, StringComparison.Ordinal))
        {
            var sub = subpath.Trim('/');
            if (sub == "" || sub == "users")       await WriteAllUsersAsync(zip, ct);
            if (sub == "" || sub == "roles")       await WriteAllRolesAsync(zip, ct);
            if (sub == "" || sub == "permissions") await WriteAllPermissionsAsync(zip, ct);
        }

        // 3. Entries + their attachments + their history.
        var query = clientQuery with
        {
            FilterSchemaNames = new(), // export everything regardless of schema
            RetrieveJsonPayload = true,
            Limit = clientQuery.Limit <= 0 || clientQuery.Limit > QueryLimit ? QueryLimit : clientQuery.Limit,
        };
        var rows = actor is not null
            ? await entries.QueryAsync(query, actor, policies!, ct)
            : await entries.QueryAsync(query, ct);

        foreach (var entry in rows)
        {
            try { await WriteEntryAsync(zip, entry, spaceName, ct); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "export: failed to emit entry {Space}/{Subpath}/{Shortname}",
                    entry.SpaceName, entry.Subpath, entry.Shortname);
            }
        }
    }

    // ---- export writers ----

    private async Task WriteSpaceMetaAsync(ZipArchive zip, Space space, CancellationToken ct)
    {
        var node = ToJsonObject(space, DmartJsonContext.Default.Space);
        StripMetaFields(node);
        node.Remove("subpath"); // space has no subpath at rest
        await WriteJsonAsync(zip, $"{space.Shortname}/.dm/meta.space.json", node, ct);
    }

    private async Task WriteAllUsersAsync(ZipArchive zip, CancellationToken ct)
    {
        var q = new Query
        {
            Type = QueryType.Search, SpaceName = MgmtSpace, Subpath = "/users",
            FilterSchemaNames = new(), Limit = QueryLimit, RetrieveJsonPayload = true,
        };
        var all = await users.QueryAsync(q, ct);
        foreach (var u in all)
        {
            var node = ToJsonObject(u, DmartJsonContext.Default.User);
            StripMetaFields(node);
            node.Remove("subpath");
            // User payload body externalized next to `management/users/.dm/{sn}/`,
            // i.e. `management/users/{sn}.json`.
            var bodyBase = $"{MgmtSpace}/users";
            await MaybeExternalizePayloadBodyAsync(zip, node, bodyBase, u.Shortname, ct);
            await WriteJsonAsync(zip, $"{MgmtSpace}/users/.dm/{u.Shortname}/meta.user.json", node, ct);
        }
    }

    private async Task WriteAllRolesAsync(ZipArchive zip, CancellationToken ct)
    {
        var q = new Query
        {
            Type = QueryType.Search, SpaceName = MgmtSpace, Subpath = "/roles",
            FilterSchemaNames = new(), Limit = QueryLimit,
        };
        var all = await access.QueryRolesAsync(q, ct);
        foreach (var r in all)
        {
            var node = ToJsonObject(r, DmartJsonContext.Default.Role);
            StripMetaFields(node);
            node.Remove("subpath");
            await WriteJsonAsync(zip, $"{MgmtSpace}/roles/.dm/{r.Shortname}/meta.role.json", node, ct);
        }
    }

    private async Task WriteAllPermissionsAsync(ZipArchive zip, CancellationToken ct)
    {
        var q = new Query
        {
            Type = QueryType.Search, SpaceName = MgmtSpace, Subpath = "/permissions",
            FilterSchemaNames = new(), Limit = QueryLimit,
        };
        var all = await access.QueryPermissionsAsync(q, ct);
        foreach (var p in all)
        {
            var node = ToJsonObject(p, DmartJsonContext.Default.Permission);
            StripMetaFields(node);
            node.Remove("subpath");
            await WriteJsonAsync(zip, $"{MgmtSpace}/permissions/.dm/{p.Shortname}/meta.permission.json", node, ct);
        }
    }

    private async Task WriteEntryAsync(ZipArchive zip, Entry entry, string spaceName, CancellationToken ct)
    {
        var rt = JsonbHelpers.EnumMember(entry.ResourceType);
        var subpathClean = entry.Subpath.Trim('/');
        var basePath = string.IsNullOrEmpty(subpathClean) ? spaceName : $"{spaceName}/{subpathClean}";
        var isFolder = entry.ResourceType == ResourceType.Folder;

        // Python placement:
        //   folder:     {space}/{subpath}/{sn}/.dm/meta.folder.json
        //               body (if any): {space}/{subpath}/{sn}.json
        //   non-folder: {space}/{subpath}/.dm/{sn}/meta.{rt}.json
        //               body (if any): {space}/{subpath}/{sn}.json
        //               attachments:   {space}/{subpath}/.dm/{sn}/attachments.{rt}/...
        //               history:       {space}/{subpath}/.dm/{sn}/history.jsonl
        string metaPath, metaDir;
        if (isFolder)
        {
            metaDir = $"{basePath}/{entry.Shortname}/.dm";
            metaPath = $"{metaDir}/meta.folder.json";
        }
        else
        {
            metaDir = $"{basePath}/.dm/{entry.Shortname}";
            metaPath = $"{metaDir}/meta.{rt}.json";
        }

        var node = ToJsonObject(entry, DmartJsonContext.Default.Entry);
        StripMetaFields(node);
        if (entry.ResourceType != ResourceType.Ticket) StripTicketFields(node);

        await MaybeExternalizePayloadBodyAsync(zip, node, basePath, entry.Shortname, ct);
        await WriteJsonAsync(zip, metaPath, node, ct);

        if (!isFolder)
        {
            await WriteAttachmentsAsync(zip, spaceName, entry, metaDir, ct);
            await WriteHistoryAsync(zip, entry, metaDir, ct);
        }
    }

    // Externalize payload.body per Python's rules:
    //   - content_type = json: write body to `{baseDir}/{shortname}.json`,
    //     rewrite meta.payload.body to the filename string.
    //   - content_type = html: write body to `{baseDir}/{shortname}.html`.
    //   - content_type = text / markdown: same idea with .txt / .md extensions.
    //   - image types: if the body is already a filename string (has ext),
    //     leave it (points to a file in the same dir — we can't produce the
    //     bytes from the wire representation reliably). If the body is a
    //     base64 string, decode it and write with the sub-type extension.
    //   - empty body → nothing to externalize, keep meta.payload.body as-is.
    private async Task MaybeExternalizePayloadBodyAsync(
        ZipArchive zip, JsonObject metaNode, string baseDir, string shortname, CancellationToken ct)
    {
        if (metaNode["payload"] is not JsonObject payload) return;
        if (payload["body"] is not JsonNode bodyNode) return;
        var contentType = (payload["content_type"]?.GetValue<string>() ?? "json").ToLowerInvariant();

        switch (contentType)
        {
            case "json":
            {
                if (bodyNode is JsonObject || bodyNode is JsonArray)
                {
                    var filename = $"{shortname}.json";
                    await WriteTextAsync(zip, $"{baseDir}/{filename}", bodyNode.ToJsonString(), ct);
                    payload["body"] = filename;
                }
                break;
            }
            case "html":
            {
                if (TryGetStringBody(bodyNode, out var html))
                {
                    var filename = $"{shortname}.html";
                    await WriteTextAsync(zip, $"{baseDir}/{filename}", html, ct);
                    payload["body"] = filename;
                }
                break;
            }
            case "text":
            case "markdown":
            {
                if (TryGetStringBody(bodyNode, out var txt))
                {
                    var ext = contentType == "markdown" ? "md" : "txt";
                    var filename = $"{shortname}.{ext}";
                    await WriteTextAsync(zip, $"{baseDir}/{filename}", txt, ct);
                    payload["body"] = filename;
                }
                break;
            }
            // image / binary types: can't recover bytes from the wire form
            // if the body is already a filename string. If it's base64 we
            // could decode, but we don't bother — Python's path here only
            // works when the original on-disk file is still present. For
            // round-trip parity from C# → C#, the db-only types are round
            // tripped via meta+inline body, not externalization.
        }
    }

    private async Task WriteAttachmentsAsync(
        ZipArchive zip, string spaceName, Entry parent, string parentMetaDir, CancellationToken ct)
    {
        // AttachmentRepository keys attachments by (space, parent_subpath/parent_sn, att_sn).
        // parent.Subpath is the parent's OWN subpath; its children live at subpath "/parent/child".
        var attList = await attachments.ListForParentAsync(spaceName, parent.Subpath, parent.Shortname, ct);
        foreach (var att in attList)
        {
            var attRt = JsonbHelpers.EnumMember(att.ResourceType);
            var attDir = $"{parentMetaDir}/attachments.{attRt}";
            var node = ToJsonObject(att with { Media = null }, DmartJsonContext.Default.Attachment);
            StripMetaFields(node);
            // Parent identity is encoded in the attachment's subpath ("/parent_subpath/parent_sn").
            // Retain that on disk so the import side can reconstruct the FK.

            // Body externalization for attachments — Python writes a JSON body
            // to `{attachments.{rt}}/{att_sn}.json` and rewrites
            // payload.body = "{att_sn}.json". For non-JSON (image/media) it
            // writes the raw media bytes to payload.body's filename.
            if (node["payload"] is JsonObject p)
            {
                var ct0 = (p["content_type"]?.GetValue<string>() ?? "").ToLowerInvariant();
                if (ct0 == "json" && p["body"] is JsonNode jbody && (jbody is JsonObject || jbody is JsonArray))
                {
                    var filename = $"{att.Shortname}.json";
                    await WriteTextAsync(zip, $"{attDir}/{filename}", jbody.ToJsonString(), ct);
                    p["body"] = filename;
                }
                else if (att.Media is { Length: > 0 } bytes
                    && p["body"] is JsonValue mediaName
                    && mediaName.TryGetValue<string>(out var mediaFilename)
                    && !string.IsNullOrEmpty(mediaFilename))
                {
                    await WriteBytesAsync(zip, $"{attDir}/{mediaFilename}", bytes, ct);
                }
                // else: Python's fallthrough — skip media write when we don't
                // have a filename to use. No `{att_sn}.bin` fallback per user
                // request.
            }
            // Attachment meta path: `attachments.{rt}/meta.{att_sn}.json`
            // (Python convention — NOT the same shape as entry metas).
            await WriteJsonAsync(zip, $"{attDir}/meta.{att.Shortname}.json", node, ct);
        }
    }

    private async Task WriteHistoryAsync(
        ZipArchive zip, Entry parent, string parentMetaDir, CancellationToken ct)
    {
        // HistoryRepository exposes a Query-typed reader; build a minimal one
        // for a single entry so we capture that entry's history column.
        var q = new Query
        {
            Type = QueryType.History, SpaceName = parent.SpaceName,
            Subpath = parent.Subpath, FilterShortnames = new() { parent.Shortname },
            Limit = QueryLimit,
        };
        List<HistoryRecord> rows;
        try { rows = await histories.QueryHistoryAsync(q, ct); }
        catch (Exception ex) { log.LogWarning(ex, "history export skipped"); return; }
        if (rows.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var h in rows)
        {
            // Python writes each history row as a JSON object (one per line)
            // with `shortname = "history"` and space_name/subpath/resource_type
            // stripped. We mirror that shape so re-import works.
            var hNode = new JsonObject
            {
                ["uuid"] = h.Uuid.ToString(),
                ["shortname"] = "history",
                ["owner_shortname"] = h.OwnerShortname,
                ["timestamp"] = h.Timestamp.ToString("o"),
                ["request_headers"] = h.RequestHeaders is null ? null : JsonNode.Parse(h.RequestHeaders),
                ["diff"] = h.Diff is null ? null : JsonNode.Parse(h.Diff),
            };
            sb.Append(hNode.ToJsonString()).Append('\n');
        }
        await WriteTextAsync(zip, $"{parentMetaDir}/history.jsonl", sb.ToString(), ct);
    }

    // ========================================================================
    // IMPORT
    // ========================================================================

    public async Task<Response> ImportZipAsync(Stream zip, string? actor, CancellationToken ct = default)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);

        // Layout validation — hard-fail on the legacy flat C# layout. Every
        // non-empty entry must live under `{space}/…` where `{space}` has no
        // leading slash and no `.dm` segment at position 0. Directories with
        // a dot extension (like `.DS_Store`) get ignored.
        foreach (var ze in archive.Entries)
        {
            if (string.IsNullOrEmpty(ze.FullName) || ze.FullName.EndsWith("/")) continue;
            var first = ze.FullName.IndexOf('/');
            if (first <= 0 || ze.FullName.StartsWith(".dm/", StringComparison.Ordinal))
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    $"zip entry '{ze.FullName}' is not under a top-level space directory — "
                    + "legacy flat layout is not supported, re-export with the current release",
                    ErrorTypes.Request);
        }

        // Group entries by their space root (first path segment) and
        // classify each by role: space meta / user / role / permission /
        // entry meta / attachment meta / attachment body / history / body file.
        var results = new ImportStats();
        var zes = archive.Entries
            .Where(z => !string.IsNullOrEmpty(z.FullName) && !z.FullName.EndsWith("/"))
            .ToList();

        // ---- Pass 1: Spaces (so FKs are ready) ----
        foreach (var ze in zes.Where(z => z.FullName.EndsWith("/.dm/meta.space.json", StringComparison.Ordinal)))
            await TryImportSpaceAsync(ze, results, ct);

        // ---- Pass 2: Users, Roles, Permissions (management/*) ----
        foreach (var ze in zes.Where(IsUserMeta))        await TryImportUserAsync(ze, results, ct);
        foreach (var ze in zes.Where(IsRoleMeta))        await TryImportRoleAsync(ze, results, ct);
        foreach (var ze in zes.Where(IsPermissionMeta))  await TryImportPermissionAsync(ze, results, ct);

        // ---- Pass 3: Entries (including folders). Index bodies first so we
        //              can re-inline them while reading the meta. ----
        var bodyLookup = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var ze in zes)
            if (!ze.FullName.Contains("/.dm/", StringComparison.Ordinal))
                bodyLookup[ze.FullName] = ze;

        foreach (var ze in zes.Where(IsEntryMeta))
            await TryImportEntryAsync(ze, bodyLookup, results, ct);

        // ---- Pass 4: Attachments. Lookup binary/json bodies from the same
        //              attachments dir. ----
        var attachmentBodies = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var ze in zes)
            if (ze.FullName.Contains("/attachments.", StringComparison.Ordinal) && !ze.Name.StartsWith("meta.", StringComparison.Ordinal))
                attachmentBodies[ze.FullName] = ze;

        foreach (var ze in zes.Where(IsAttachmentMeta))
            await TryImportAttachmentAsync(ze, attachmentBodies, results, ct);

        // ---- Pass 5: Histories ----
        foreach (var ze in zes.Where(z => z.Name == "history.jsonl"))
            await TryImportHistoryAsync(ze, results, ct);

        return Response.Ok(attributes: new()
        {
            ["entries_inserted"] = results.EntriesInserted,
            ["attachments_inserted"] = results.AttachmentsInserted,
            ["spaces_inserted"] = results.SpacesInserted,
            ["users_inserted"] = results.UsersInserted,
            ["roles_inserted"] = results.RolesInserted,
            ["permissions_inserted"] = results.PermissionsInserted,
            ["histories_inserted"] = results.HistoriesInserted,
            ["failed_count"] = results.Failed.Count,
            ["failed"] = results.Failed,
        });
    }

    // ---- layout classifiers ----

    private static bool IsUserMeta(ZipArchiveEntry ze)       => MatchDotDmMeta(ze, "users", "user");
    private static bool IsRoleMeta(ZipArchiveEntry ze)       => MatchDotDmMeta(ze, "roles", "role");
    private static bool IsPermissionMeta(ZipArchiveEntry ze) => MatchDotDmMeta(ze, "permissions", "permission");
    private static bool MatchDotDmMeta(ZipArchiveEntry ze, string parent, string metaType)
        => ze.FullName.Contains($"/{parent}/.dm/", StringComparison.Ordinal)
           && ze.Name == $"meta.{metaType}.json";

    private static bool IsEntryMeta(ZipArchiveEntry ze)
    {
        if (!ze.FullName.Contains("/.dm/", StringComparison.Ordinal)) return false;
        if (!ze.Name.StartsWith("meta.", StringComparison.Ordinal)) return false;
        if (!ze.Name.EndsWith(".json", StringComparison.Ordinal)) return false;
        if (ze.Name == "meta.space.json") return false;
        if (ze.Name is "meta.user.json" or "meta.role.json" or "meta.permission.json") return false;
        // Attachment metas live in a `/attachments.{rt}/` subfolder, not directly under .dm.
        if (ze.FullName.Contains("/attachments.", StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool IsAttachmentMeta(ZipArchiveEntry ze)
        => ze.FullName.Contains("/attachments.", StringComparison.Ordinal)
           && ze.Name.StartsWith("meta.", StringComparison.Ordinal)
           && ze.Name.EndsWith(".json", StringComparison.Ordinal);

    // ---- importers ----

    private async Task TryImportSpaceAsync(ZipArchiveEntry ze, ImportStats st, CancellationToken ct)
    {
        try
        {
            var (spaceName, _) = SplitSpaceAndRest(ze.FullName);
            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["shortname"] ??= spaceName;
            node["subpath"] = "/";
            var space = node.Deserialize(DmartJsonContext.Default.Space);
            if (space is null) { st.Failed.Add(new() { ["path"] = ze.FullName, ["error"] = "empty space meta" }); return; }
            await spaces.UpsertAsync(space, ct);
            st.SpacesInserted++;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import space failed at {Path}", ze.FullName);
            st.Failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "space", ["error"] = ex.Message });
        }
    }

    private async Task TryImportUserAsync(ZipArchiveEntry ze, ImportStats st, CancellationToken ct)
    {
        try
        {
            var (spaceName, _) = SplitSpaceAndRest(ze.FullName);
            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = "/users";
            await InlinePayloadBodyAsync(ze, node, $"{spaceName}/users", ct);
            var user = node.Deserialize(DmartJsonContext.Default.User);
            if (user is null) { st.Failed.Add(new() { ["path"] = ze.FullName, ["error"] = "empty user meta" }); return; }
            await users.UpsertAsync(user, ct);
            st.UsersInserted++;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import user failed at {Path}", ze.FullName);
            st.Failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "user", ["error"] = ex.Message });
        }
    }

    private async Task TryImportRoleAsync(ZipArchiveEntry ze, ImportStats st, CancellationToken ct)
    {
        try
        {
            var (spaceName, _) = SplitSpaceAndRest(ze.FullName);
            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = "/roles";
            var role = node.Deserialize(DmartJsonContext.Default.Role);
            if (role is null) { st.Failed.Add(new() { ["path"] = ze.FullName, ["error"] = "empty role meta" }); return; }
            await access.UpsertRoleAsync(role, ct);
            st.RolesInserted++;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import role failed at {Path}", ze.FullName);
            st.Failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "role", ["error"] = ex.Message });
        }
    }

    private async Task TryImportPermissionAsync(ZipArchiveEntry ze, ImportStats st, CancellationToken ct)
    {
        try
        {
            var (spaceName, _) = SplitSpaceAndRest(ze.FullName);
            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = "/permissions";
            var perm = node.Deserialize(DmartJsonContext.Default.Permission);
            if (perm is null) { st.Failed.Add(new() { ["path"] = ze.FullName, ["error"] = "empty permission meta" }); return; }
            await access.UpsertPermissionAsync(perm, ct);
            st.PermissionsInserted++;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import permission failed at {Path}", ze.FullName);
            st.Failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "permission", ["error"] = ex.Message });
        }
    }

    private async Task TryImportEntryAsync(
        ZipArchiveEntry ze, Dictionary<string, ZipArchiveEntry> bodyLookup,
        ImportStats st, CancellationToken ct)
    {
        try
        {
            var (spaceName, rest) = SplitSpaceAndRest(ze.FullName);
            var isFolder = ze.Name == "meta.folder.json";
            string subpath;
            if (isFolder)
            {
                // Folder meta path: "{space}/{subpath}/{folder_sn}/.dm/meta.folder.json".
                // `rest` drops the `{space}/` prefix → "{subpath}/{folder_sn}/.dm/meta.folder.json".
                var dmIdx = rest.LastIndexOf("/.dm/meta.folder.json", StringComparison.Ordinal);
                if (dmIdx < 0) throw new InvalidDataException("folder meta path malformed");
                var withFolder = rest[..dmIdx];               // {subpath}/{folder_sn}
                var lastSlash = withFolder.LastIndexOf('/');
                subpath = lastSlash < 0 ? "/" : "/" + withFolder[..lastSlash];
                // The folder's shortname comes from the directory name, so we don't
                // override the meta's shortname here — the export wrote it in.
            }
            else
            {
                // Non-folder path: "{space}/{subpath}/.dm/{sn}/meta.{rt}.json".
                // When subpath is "/" the leading slash is absent from rest,
                // so rest starts with ".dm/". Treat that specially.
                if (rest.StartsWith(".dm/", StringComparison.Ordinal))
                {
                    subpath = "/";
                }
                else
                {
                    var idx = rest.IndexOf("/.dm/", StringComparison.Ordinal);
                    if (idx < 0) throw new InvalidDataException("entry meta path missing /.dm/");
                    subpath = "/" + rest[..idx];
                }
            }

            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = subpath;
            node["resource_type"] ??= InferResourceTypeFromFilename(ze.Name);

            // Re-inline payload.body from the external file when it's a string
            // filename that points at a sibling JSON/HTML/text/image file.
            var baseDir = isFolder ? rest[..rest.IndexOf("/.dm/", StringComparison.Ordinal)].PathJoin(spaceName) : null;
            // For non-folder, baseDir is "{space}/{subpath}" (where the body file lives).
            baseDir ??= $"{spaceName}/{subpath.TrimStart('/')}".TrimEnd('/');
            // Folder body lives next to the folder directory: "{space}/{subpath}/{folder_sn}.json".
            // Which we already captured via rest (subpath parent). Compute base dir accordingly.
            if (isFolder)
            {
                var cutAt = rest.LastIndexOf("/.dm/meta.folder.json", StringComparison.Ordinal);
                // rest[..cutAt] is "{subpath}/{folder_sn}"; strip the trailing
                // {folder_sn} so baseDir is the folder's PARENT dir in the zip.
                var withFolder = rest[..cutAt];
                var lastSlash = withFolder.LastIndexOf('/');
                var parentDir = lastSlash < 0 ? "" : withFolder[..lastSlash];
                baseDir = string.IsNullOrEmpty(parentDir) ? spaceName : $"{spaceName}/{parentDir}";
            }
            InlinePayloadBody(node, baseDir, bodyLookup);

            var entry = node.Deserialize(DmartJsonContext.Default.Entry);
            if (entry is null) { st.Failed.Add(new() { ["path"] = ze.FullName, ["error"] = "empty entry meta" }); return; }

            // Import goes through the repo (not EntryService.CreateAsync) so
            // plugin hooks don't fire per-row and perm checks don't block a
            // bulk restore. This mirrors Python's bulk_insert_in_batches path.
            await entries.UpsertAsync(entry, ct);
            st.EntriesInserted++;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import entry failed at {Path}", ze.FullName);
            st.Failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "entry", ["error"] = ex.Message });
        }
    }

    private async Task TryImportAttachmentAsync(
        ZipArchiveEntry ze, Dictionary<string, ZipArchiveEntry> bodies,
        ImportStats st, CancellationToken ct)
    {
        try
        {
            // Path: "{space}/{subpath}/.dm/{parent_sn}/attachments.{rt}/meta.{att_sn}.json"
            var (spaceName, rest) = SplitSpaceAndRest(ze.FullName);
            var attachmentsIdx = rest.LastIndexOf("/attachments.", StringComparison.Ordinal);
            if (attachmentsIdx < 0) throw new InvalidDataException("attachment meta path missing /attachments./");
            var prefix = rest[..attachmentsIdx];              // "{subpath}/.dm/{parent_sn}"
            var parentSegs = prefix.Split('/');
            if (parentSegs.Length < 2 || parentSegs[^2] != ".dm")
                throw new InvalidDataException("attachment meta path malformed: parent_sn not located");
            var parentSn = parentSegs[^1];
            var parentSubpath = "/" + string.Join('/', parentSegs[..^2]);
            if (parentSubpath == "/") parentSubpath = "/";

            // Attachment's own subpath = parent_subpath/parent_sn (matches Python).
            var attSubpath = parentSubpath.TrimEnd('/') + "/" + parentSn;

            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = attSubpath;
            // resource_type is encoded in the attachments.{rt} dir name.
            var rtDir = rest[attachmentsIdx..].Split('/')[1]; // "attachments.{rt}"
            node["resource_type"] ??= rtDir.Substring("attachments.".Length);

            // Body re-inline: JSON bodies come from `{att_sn}.json`; media
            // bytes come from the filename in payload.body.
            var attDir = ze.FullName[..ze.FullName.LastIndexOf('/')];
            if (node["payload"] is JsonObject p)
            {
                var bodyField = p["body"]?.GetValue<string>();
                var contentType = p["content_type"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(bodyField))
                {
                    var bodyPath = $"{attDir}/{bodyField}";
                    if (bodies.TryGetValue(bodyPath, out var bodyZe))
                    {
                        if (contentType.Equals("json", StringComparison.OrdinalIgnoreCase))
                        {
                            await using var bs = bodyZe.Open();
                            using var sr = new StreamReader(bs);
                            var jsonText = await sr.ReadToEndAsync(ct);
                            p["body"] = JsonNode.Parse(jsonText);
                        }
                        else
                        {
                            await using var bs = bodyZe.Open();
                            using var mem = new MemoryStream();
                            await bs.CopyToAsync(mem, ct);
                            node["media"] = Convert.ToBase64String(mem.ToArray());
                        }
                    }
                }
            }

            var att = node.Deserialize(DmartJsonContext.Default.Attachment);
            if (att is null) { st.Failed.Add(new() { ["path"] = ze.FullName, ["error"] = "empty attachment meta" }); return; }
            await attachments.UpsertAsync(att, ct);
            st.AttachmentsInserted++;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import attachment failed at {Path}", ze.FullName);
            st.Failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "attachment", ["error"] = ex.Message });
        }
    }

    private async Task TryImportHistoryAsync(ZipArchiveEntry ze, ImportStats st, CancellationToken ct)
    {
        try
        {
            var (spaceName, rest) = SplitSpaceAndRest(ze.FullName);
            // history path: "{space}/{subpath}/.dm/{sn}/history.jsonl"
            var idx = rest.IndexOf("/.dm/", StringComparison.Ordinal);
            if (idx < 0) throw new InvalidDataException("history path missing /.dm/");
            var subpath = "/" + rest[..idx];
            var after = rest[(idx + "/.dm/".Length)..];
            var sn = after.Split('/')[0];

            await using var stream = ze.Open();
            using var sr = new StreamReader(stream);
            string? line;
            while ((line = await sr.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var hNode = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("not a JSON object");
                    var owner = hNode["owner_shortname"]?.GetValue<string>();
                    // History Append takes Dictionary<string, object>. Deserialize
                    // via the source-gen context so AOT trimming is happy.
                    Dictionary<string, object>? diff = null;
                    if (hNode["diff"] is JsonObject d)
                        diff = d.Deserialize(DmartJsonContext.Default.DictionaryStringObject);
                    Dictionary<string, object>? reqHeaders = null;
                    if (hNode["request_headers"] is JsonObject rh)
                        reqHeaders = rh.Deserialize(DmartJsonContext.Default.DictionaryStringObject);
                    await histories.AppendAsync(spaceName, subpath, sn, owner,
                        reqHeaders, diff, ct);
                    st.HistoriesInserted++;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "history line skipped in {Path}", ze.FullName);
                    st.Failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "history_line", ["error"] = ex.Message });
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import history failed at {Path}", ze.FullName);
            st.Failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "history", ["error"] = ex.Message });
        }
    }

    // ========================================================================
    // Helpers — JSON transforms + zip IO + path splitting
    // ========================================================================

    private sealed class ImportStats
    {
        public int EntriesInserted;
        public int AttachmentsInserted;
        public int SpacesInserted;
        public int UsersInserted;
        public int RolesInserted;
        public int PermissionsInserted;
        public int HistoriesInserted;
        public readonly List<Dictionary<string, object>> Failed = new();
    }

    private static JsonObject ToJsonObject<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => JsonSerializer.SerializeToNode(value, info)!.AsObject();

    // Python's `write_json_file` drops `query_policies` and empty values.
    // We drop query_policies unconditionally and strip space_name / subpath /
    // resource_type since those are encoded in the zip path itself (import
    // re-injects them from the path on the way back).
    private static void StripMetaFields(JsonObject o)
    {
        o.Remove("query_policies");
        o.Remove("space_name");
        o.Remove("subpath");
        o.Remove("resource_type");
    }

    // Python strips ticket fields for non-ticket entries so a Content meta
    // doesn't carry null state/is_open/etc.
    private static void StripTicketFields(JsonObject o)
    {
        o.Remove("state");
        o.Remove("is_open");
        o.Remove("reporter");
        o.Remove("workflow_shortname");
        o.Remove("collaborators");
        o.Remove("resolution_reason");
    }

    private static bool TryGetStringBody(JsonNode body, out string value)
    {
        if (body is JsonValue v && v.TryGetValue<string>(out var s)) { value = s; return true; }
        value = ""; return false;
    }

    // Re-inline the externalized payload.body from a sibling body file.
    // Mutates `metaNode` in place.
    private static void InlinePayloadBody(
        JsonObject metaNode, string baseDir, Dictionary<string, ZipArchiveEntry> bodies)
    {
        if (metaNode["payload"] is not JsonObject payload) return;
        if (payload["body"] is not JsonValue bodyVal) return;
        if (!bodyVal.TryGetValue<string>(out var filename) || string.IsNullOrEmpty(filename)) return;
        // Python stores the filename relative to the entry's subpath directory.
        var bodyPath = $"{baseDir}/{filename}".Replace("//", "/");
        if (!bodies.TryGetValue(bodyPath, out var ze)) return;
        var contentType = (payload["content_type"]?.GetValue<string>() ?? "").ToLowerInvariant();
        using var s = ze.Open();
        using var sr = new StreamReader(s);
        var raw = sr.ReadToEnd();
        if (contentType == "json")
        {
            try { payload["body"] = JsonNode.Parse(raw); }
            catch { payload["body"] = raw; /* leave as string if the file isn't JSON */ }
        }
        else
        {
            payload["body"] = raw;
        }
    }

    private async Task InlinePayloadBodyAsync(
        ZipArchiveEntry metaZe, JsonObject metaNode, string baseDir, CancellationToken ct)
    {
        if (metaNode["payload"] is not JsonObject payload) return;
        if (payload["body"] is not JsonValue bodyVal) return;
        if (!bodyVal.TryGetValue<string>(out var filename) || string.IsNullOrEmpty(filename)) return;
        var bodyPath = $"{baseDir}/{filename}".Replace("//", "/");
        var archive = metaZe.Archive!;
        var bodyZe = archive.GetEntry(bodyPath);
        if (bodyZe is null) return;
        await using var s = bodyZe.Open();
        using var sr = new StreamReader(s);
        var raw = await sr.ReadToEndAsync(ct);
        var contentType = (payload["content_type"]?.GetValue<string>() ?? "").ToLowerInvariant();
        if (contentType == "json")
        {
            try { payload["body"] = JsonNode.Parse(raw); }
            catch { payload["body"] = raw; }
        }
        else payload["body"] = raw;
    }

    private static (string space, string rest) SplitSpaceAndRest(string fullName)
    {
        var slash = fullName.IndexOf('/');
        if (slash <= 0) return (fullName, "");
        return (fullName[..slash], fullName[(slash + 1)..]);
    }

    private static string InferResourceTypeFromFilename(string metaFileName)
    {
        // "meta.{rt}.json" → "{rt}"
        const string prefix = "meta.";
        const string suffix = ".json";
        if (!metaFileName.StartsWith(prefix, StringComparison.Ordinal) || !metaFileName.EndsWith(suffix, StringComparison.Ordinal))
            return "content";
        return metaFileName[prefix.Length..^suffix.Length];
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(ZipArchiveEntry ze, CancellationToken ct)
    {
        await using var s = ze.Open();
        var node = await JsonNode.ParseAsync(s, cancellationToken: ct);
        return node as JsonObject
            ?? throw new InvalidDataException($"{ze.FullName}: expected a JSON object at the root");
    }

    private static async Task WriteJsonAsync(ZipArchive zip, string path, JsonObject node, CancellationToken ct)
        => await WriteTextAsync(zip, path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), ct);

    private static async Task WriteTextAsync(ZipArchive zip, string path, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await s.WriteAsync(bytes, ct);
    }

    private static async Task WriteBytesAsync(ZipArchive zip, string path, byte[] bytes, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await s.WriteAsync(bytes, ct);
    }
}

// Tiny extension used once above to keep the path construction readable.
internal static class PathJoinExtensions
{
    internal static string PathJoin(this string head, string prefix)
        => string.IsNullOrEmpty(head) ? prefix : $"{prefix}/{head}";
}
