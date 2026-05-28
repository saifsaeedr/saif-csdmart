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
using Npgsql;
using NpgsqlTypes;

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
    Db db,
    IOptions<DmartSettings> settingsOpt,
    ILogger<ImportExportService> log)
{
    private const int QueryLimit = 100_000;

    // Maximum rows accumulated in memory before a bulk COPY flush. Bounds
    // peak working-set of `bulkEntries` / `bulkAttachments` independent of
    // total import size — without this, a single space with millions of
    // entries would materialize every parsed Entry (with inlined JSON
    // payload) in RAM before the one COPY at the end of RunTailPassesAsync.
    // At ~10k rows × ~10 KB avg payload, the accumulator caps around
    // ~100 MB; operators with larger payloads should lower via --batch-size.
    public const int DefaultBatchSize = 10_000;

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

    private static async Task WriteSpaceMetaAsync(ZipArchive zip, Space space, CancellationToken ct)
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
    private static async Task MaybeExternalizePayloadBodyAsync(
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

    public Task<Response> ImportZipAsync(Stream zip, string? actor, CancellationToken ct = default)
        => ImportZipAsync(zip, actor, preserveExisting: false, fastUnsafeNoFkCheck: false, fastParallelism: 1, ct: ct);

    public Task<Response> ImportZipAsync(Stream zip, string? actor, bool preserveExisting, CancellationToken ct = default)
        => ImportZipAsync(zip, actor, preserveExisting, fastUnsafeNoFkCheck: false, fastParallelism: 1, ct: ct);

    public Task<Response> ImportZipAsync(Stream zip, string? actor, bool preserveExisting, bool fastUnsafeNoFkCheck, CancellationToken ct = default)
        => ImportZipAsync(zip, actor, preserveExisting, fastUnsafeNoFkCheck, fastParallelism: 1, ct: ct);

    /// <summary>
    /// Import a dmart zip into the database.
    /// </summary>
    /// <param name="preserveExisting">
    /// When true, rows that already exist (matched by the same key the
    /// repository's UpsertAsync would conflict on) are skipped — the in-DB
    /// values are left untouched. The CLI <c>dmart import</c> uses this so
    /// re-running an import doesn't blow away local edits. When false (the
    /// default and the HTTP <c>/managed/import</c> contract), every row is
    /// upserted: existing rows get their non-key columns replaced from the
    /// zip's meta.
    /// </param>
    /// <param name="fastUnsafeNoFkCheck">
    /// When true, the entire import runs on a single Npgsql session with
    /// <c>session_replication_role = 'replica'</c>, which bypasses ALL FK
    /// constraints and user-defined triggers for the duration of the import.
    /// Used by the standalone CLI <c>dmart import --fast</c> path where the
    /// zip is operator-trusted. Hard-fails with a clear error if the DB role
    /// can't grant that privilege — no silent fallback. The HTTP
    /// <c>/managed/import</c> contract must never set this true.
    /// </param>
    /// <param name="fastParallelism">
    /// When &gt; 1 AND <paramref name="fastUnsafeNoFkCheck"/> is true AND the
    /// zip carries more than one space, Pass 3 (entries), Pass 4 (attachments)
    /// and Pass 5 (histories) run on separate per-space worker connections
    /// in parallel, each with its own transaction. Trade-off: the
    /// single-transaction-for-the-whole-import property is dropped — a crash
    /// mid-import leaves some spaces fully landed and others not (operator
    /// re-runs with -r/--replace to retry). Default 1 = serial, identical to
    /// Checkpoint ALPHA behaviour. Ignored when <paramref name="fastUnsafeNoFkCheck"/>
    /// is false — would have no effect since the slow path has no scoped
    /// session to share. Clamped to <c>[1, 16]</c> defensively.
    /// </param>
    public async Task<Response> ImportZipAsync(Stream zip, string? actor, bool preserveExisting, bool fastUnsafeNoFkCheck, int fastParallelism, int batchSize = DefaultBatchSize, CancellationToken ct = default)
    {
        // `actor` is accepted for API stability but no longer threaded through —
        // every imported record's owner comes from its meta's owner_shortname,
        // and EnsureOwner backstops malformed exports with a literal "dmart"
        // (which is guaranteed to exist via AdminBootstrap).
        _ = actor;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entries = archive.Entries
            .Where(z => !string.IsNullOrEmpty(z.FullName) && !z.FullName.EndsWith("/"))
            .Select(ImportEntryRef.FromZip)
            .ToList();
        // Zip imports don't get a checkpoint sidecar — there's no obvious
        // place to put it when the zip lives on remote / read-only storage.
        // 24M-target migrations use the fs path; zip remains the small-
        // scale convenience.
        // Zip imports don't get validation today — the validator's schema
        // cache needs filesystem layout to resolve schemas. Threading zip
        // entries to a synthetic root would work but is out of scope for
        // now. Operators wanting validation use the filesystem path.
        return await ImportFromEntriesAsync(entries, ImportSourceKind.Zip,
            preserveExisting, fastUnsafeNoFkCheck, fastParallelism, batchSize, checkpoint: null, validation: null, importTags: null, ct: ct);
    }

    public Task<Response> ImportFolderAsync(string folderPath, string? actor, CancellationToken ct = default)
        => ImportFolderAsync(folderPath, actor, preserveExisting: false, fastUnsafeNoFkCheck: false, fastParallelism: 1, ct: ct);

    public Task<Response> ImportFolderAsync(string folderPath, string? actor, bool preserveExisting, CancellationToken ct = default)
        => ImportFolderAsync(folderPath, actor, preserveExisting, fastUnsafeNoFkCheck: false, fastParallelism: 1, ct: ct);

    public Task<Response> ImportFolderAsync(string folderPath, string? actor, bool preserveExisting, bool fastUnsafeNoFkCheck, CancellationToken ct = default)
        => ImportFolderAsync(folderPath, actor, preserveExisting, fastUnsafeNoFkCheck, fastParallelism: 1, ct: ct);

    /// <summary>
    /// Import a folder laid out like the inside of a dmart export zip
    /// (one or more <c>{space}/</c> directories at the top level). All
    /// semantics — preserveExisting, --fast, parallelism — match
    /// <see cref="ImportZipAsync"/>; the only difference is that file
    /// bytes are read directly from disk instead of from a zip archive.
    /// </summary>
    /// <param name="targetSpace">
    /// Optional remap: when set, the source folder is treated as content
    /// to import INTO the named space (which must already exist) at
    /// <paramref name="targetSubpath"/>. The layout expectation flips:
    /// the source must NOT contain a <c>meta.space.json</c> anywhere
    /// (that would be ambiguous), and every relative path inside the
    /// source has <c>{targetSpace}/{targetSubpath}/</c> prepended before
    /// the layout validator runs. Both <paramref name="targetSpace"/>
    /// and <paramref name="targetSubpath"/> must be supplied together,
    /// or both left null for the legacy "source is a full space dump"
    /// behaviour.
    /// </param>
    /// <param name="targetSubpath">
    /// Optional parent subpath inside the target space (e.g.
    /// <c>"/products"</c>). Normalized — leading and trailing slashes
    /// stripped, <c>""</c> or <c>"/"</c> means "directly under the space
    /// root". Required when <paramref name="targetSpace"/> is set.
    /// </param>
    public async Task<Response> ImportFolderAsync(string folderPath, string? actor, bool preserveExisting, bool fastUnsafeNoFkCheck, int fastParallelism, int batchSize = DefaultBatchSize, string? targetSpace = null, string? targetSubpath = null, bool resume = false, string? checkpointPath = null, DateTime? sinceUtc = null, bool validate = true, string? issuesFilePath = null, bool skipHistory = false, IReadOnlyList<string>? importTags = null, CancellationToken ct = default)
    {
        _ = actor;
        if (!Directory.Exists(folderPath))
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                $"import folder '{folderPath}' does not exist or is not a directory",
                ErrorTypes.Request);

        // Validate the remap-mode parameter pair. Both must be set, or
        // both null — partial usage is almost certainly an operator
        // mistake (e.g. typo on one of the two flag names).
        var remapMode = !string.IsNullOrEmpty(targetSpace) || !string.IsNullOrEmpty(targetSubpath);
        string? normalizedSubpath = null;
        if (remapMode)
        {
            if (string.IsNullOrEmpty(targetSpace))
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    "targetSubpath set without targetSpace — both are required together",
                    ErrorTypes.Request);
            if (targetSubpath is null)
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    "targetSpace set without targetSubpath — pass '/' for the space root",
                    ErrorTypes.Request);

            // Normalize the subpath: strip leading and trailing slashes so
            // "/products", "products", "/products/", "products/" all mean
            // the same thing. The space root is represented as empty.
            normalizedSubpath = targetSubpath.Trim().Trim('/');

            // Pre-flight: target space must already exist in the DB. Remap
            // mode is "drop content into an existing space" — never
            // "create a new space from a source that doesn't have its
            // own space meta." Catches the operator typo early.
            if (await spaces.GetAsync(targetSpace, ct) is null)
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    $"target space '{targetSpace}' does not exist — create it first " +
                    "or invoke without --space to import a full space dump",
                    ErrorTypes.Request);
        }
        else
        {
            // Legacy "full space dump" sanity check: at least one immediate
            // subdirectory must look like a dmart space (carry
            // .dm/meta.space.json). Cheap to compute, catches typos that
            // point the import at the wrong directory before we burn a
            // fast-import session on nothing.
            var hasSpace = Directory.EnumerateDirectories(folderPath)
                .Any(d => File.Exists(Path.Combine(d, ".dm", "meta.space.json")));
            if (!hasSpace)
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    $"target folder '{folderPath}' does not look like a dmart space dump "
                    + "(no */.dm/meta.space.json found) — pass --space and --subpath "
                    + "to import this folder as content into an existing space",
                    ErrorTypes.Request);
        }

        // Skip dot-prefixed segments (e.g. `.git/`, `.DS_Store`, vim swap
        // `.foo.swp`, editor backup state) EXCEPT `.dm/` which is
        // load-bearing in the dmart layout. The check runs on every path
        // segment so a file like `space/.git/HEAD` is rejected even though
        // its filename doesn't itself start with a dot.
        static bool ShouldSkip(string relPath)
        {
            foreach (var seg in relPath.Split('/'))
                if (seg.Length > 0 && seg[0] == '.' && seg != ".dm")
                    return true;
            return false;
        }

        // EnumerationOptions.AttributesToSkip = ReparsePoint guards against
        // symlinks pointing outside the import folder (e.g. /etc/passwd).
        // Operator-trusted CLI usage rarely needs symlink traversal, and
        // the cost of skipping is "the operator can't symlink their dump"
        // — acceptable in exchange for the safety property.
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };

        // Remap prefix: empty string when remap mode is off; otherwise
        // "{space}/" or "{space}/{subpath}/" so every entry's relative
        // path becomes a valid `{space}/...` after concatenation.
        var prefix = remapMode
            ? (string.IsNullOrEmpty(normalizedSubpath)
                ? $"{targetSpace}/"
                : $"{targetSpace}/{normalizedSubpath}/")
            : "";

        var entries = new List<ImportEntryRef>();
        long mtimeSkipped = 0;
        foreach (var abs in Directory.EnumerateFiles(folderPath, "*", enumOpts))
        {
            var rel = Path.GetRelativePath(folderPath, abs).Replace(Path.DirectorySeparatorChar, '/');
            if (ShouldSkip(rel)) continue;

            // --since: drop entries whose file mtime is older than the cutoff.
            // One stat() per surviving path; no file read. Uses mtime as a
            // proxy for the meta's updated_at — they match in practice because
            // dmart writes meta.<rt>.json on every entry update. Operators
            // with rsync-mangled mtimes should not use this flag.
            if (sinceUtc.HasValue)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(abs) < sinceUtc.Value)
                    {
                        mtimeSkipped++;
                        continue;
                    }
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
            }

            // Remap mode: reject any source-level space meta. The operator
            // said "import into {targetSpace}", so a meta.space.json in
            // the source is ambiguous — either they meant a full-dump
            // import (in which case --space is wrong) or they have a
            // misplaced meta.space.json in their tree (operator error).
            // Either way, fail loudly instead of silently overwriting
            // the target space's own meta.
            if (remapMode && rel.EndsWith("/.dm/meta.space.json", StringComparison.Ordinal))
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    $"remap mode (--space/--subpath) found a space meta in the source at '{rel}' — " +
                    "remap is for content-only imports into an existing space, " +
                    "remove the meta.space.json or invoke without --space",
                    ErrorTypes.Request);

            entries.Add(ImportEntryRef.FromFile(prefix + rel, abs));
        }

        if (sinceUtc.HasValue)
        {
            log.LogInformation(
                "import: --since kept {Kept} entries, skipped {MtimeSkipped} by mtime",
                entries.Count, mtimeSkipped);
        }

        // --skip-history: drop history.jsonl from the entry set so Pass 5
        // (histories) finds nothing. History is an audit trail, not current
        // state — often unwanted in a migration target, frequently carries
        // sensitive diffs (password changes), a large fraction of on-disk
        // bytes, and the place data-quality issues cluster (embedded NULs).
        if (skipHistory)
        {
            var before = entries.Count;
            entries.RemoveAll(e => e.Name == "history.jsonl");
            log.LogInformation("import: --skip-history dropped {Count} history.jsonl files", before - entries.Count);
        }

        // Resume support: only meaningful for filesystem imports (zip
        // sidecar location is operator-unfriendly when the zip lives on
        // remote storage). When --resume is set, load (or create) the
        // checkpoint file and pass it down so the orchestrator can skip
        // already-committed passes / spaces.
        ImportCheckpointStore? checkpoint = null;
        if (resume)
        {
            var path = checkpointPath ?? ImportCheckpointStore.DefaultPathFor(folderPath);
            checkpoint = ImportCheckpointStore.LoadOrCreate(path, folderPath);
        }

        // Validation context: when enabled (default), per-entry validators
        // fire during the tail passes — owner remap, uuid dedup, schema
        // check. Issues land in a JSONL sidecar at issuesFilePath (default:
        // <folder>/import-issues-<timestamp>.jsonl). Source files are
        // never mutated; fixes happen in memory before the bulk COPY.
        ImportValidationContext? validation = null;
        if (validate)
        {
            var sidecar = issuesFilePath ?? Path.Combine(folderPath,
                $"import-issues-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");
            validation = new ImportValidationContext(folderPath, new ImportIssueSink(sidecar), log);

            // Seed the known-users set from existing PG rows so re-runs and
            // partial imports don't falsely flag owners that are valid in
            // PG but happen not to appear in the current source. Cheap —
            // shortname is indexed and we only need the column.
            try
            {
                await using var seedConn = await db.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT shortname FROM users", seedConn);
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                var seeded = 0;
                while (await rdr.ReadAsync(ct))
                {
                    var sn = rdr.GetString(0);
                    validation.RegisterKnownUser(sn);
                    seeded++;
                }
                log.LogInformation("import: validation seeded with {Count} existing PG users", seeded);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "import: failed to seed validation user set from PG; only source-side users will be valid");
            }

            log.LogInformation("import: validation enabled, sidecar at {Path}", sidecar);
        }

        Response response;
        try
        {
            response = await ImportFromEntriesAsync(entries, ImportSourceKind.Filesystem,
                preserveExisting, fastUnsafeNoFkCheck, fastParallelism, batchSize, checkpoint, validation, importTags, ct);
        }
        finally
        {
            if (validation is not null)
            {
                log.LogInformation("import: {Count} validation issues logged to sidecar", validation.Sink.Count);
                await validation.DisposeAsync();
            }
        }
        // Clean up the sidecar on a clean import — leaves a tidy
        // source tree for the next operator action. A failed import
        // leaves the checkpoint in place so the next `--resume`
        // picks up where we stopped.
        if (response.Status == Status.Success && checkpoint is not null)
            checkpoint.Clear();
        return response;
    }

    // Same pipeline that drives ImportZipAsync, but source-agnostic so the
    // folder importer (ImportFolderAsync) can share it. `sourceKind` drives
    // (a) the noun used in error messages and (b) whether the parallel-tail
    // prefetch step runs — only zip needs it (ZipArchive isn't thread-safe);
    // filesystem sources skip the prefetch to avoid an O(body bytes)
    // memory spike on large imports.
    //
    // Precondition: callers must filter out directory entries (those ending
    // in `/` for zip; conceptually irrelevant for filesystem since
    // EnumerateFiles already returns only files). The layout validator
    // below assumes every entry is a file with a non-empty path.
    private async Task<Response> ImportFromEntriesAsync(
        IReadOnlyList<ImportEntryRef> entries, ImportSourceKind sourceKind,
        bool preserveExisting, bool fastUnsafeNoFkCheck, int fastParallelism,
        int batchSize, ImportCheckpointStore? checkpoint = null,
        ImportValidationContext? validation = null, IReadOnlyList<string>? importTags = null,
        CancellationToken ct = default)
    {
        // Clamp batch size — operators tune via --batch-size for their data
        // shape (smaller for fat payloads, larger for tiny ones). The hard
        // floor of 1 guarantees the batched loop always makes progress, and
        // the ceiling of 1M is a sanity cap (above that, peak working-set
        // approaches the pre-batching world even with --fast).
        batchSize = Math.Clamp(batchSize, 1, 1_000_000);
        // Layout validation — hard-fail on the legacy flat C# layout. Every
        // entry must live under `{space}/…` where `{space}` has no leading
        // slash and no `.dm` segment at position 0.
        var sourceNoun = sourceKind.Describe();
        foreach (var ze in entries)
        {
            var first = ze.FullName.IndexOf('/');
            if (first <= 0 || ze.FullName.StartsWith(".dm/", StringComparison.Ordinal))
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    $"{sourceNoun} '{ze.FullName}' is not under a top-level space directory — "
                    + "legacy flat layout is not supported, re-export with the current release",
                    ErrorTypes.Request);
        }

        // Global path → entry dict used by InlinePayloadBodyAsync to resolve
        // sibling body files during the user head pass. Only built when an
        // import actually contains user meta files — most operator imports
        // don't, and building a 100k-entry dict eagerly for nothing is a
        // measurable cost on large dumps. Tail passes (Pass 3-5) build their
        // own per-pass body lookups from a slice of entries, so they don't
        // need this dict.
        IReadOnlyDictionary<string, ImportEntryRef> allByPath =
            entries.Any(IsUserMeta)
                ? entries.ToDictionary(e => e.FullName, e => e, StringComparer.Ordinal)
                : new Dictionary<string, ImportEntryRef>(StringComparer.Ordinal);

        var results = new ImportStats();

        // Clamp parallelism defensively. <=1 means serial (today's behaviour).
        // >16 is rejected as nonsense for a CLI import — the Npgsql default
        // pool is 10+10 and we want headroom for whatever else is connected.
        var workers = Math.Clamp(fastParallelism, 1, 16);

        // ---- Pass 1+2: Users, Spaces, Roles, Permissions ----
        // These are small and load-bearing for everything that follows; run
        // sequentially on a single shared session (one tx) regardless of
        // whether we parallelize Pass 3-5 below. Committing this scope before
        // dispatching the per-space workers ensures every worker reads
        // committed data when checking preserveExisting.
        // When --resume picked up a checkpoint that says head is done,
        // skip the whole pass — the rows are already in the DB.
        if (checkpoint?.IsHeadDone() == true)
        {
            log.LogInformation("import: --resume skipping head pass (users/spaces/roles/permissions) — checkpoint says done");
        }
        else
        {
            NpgsqlConnection? headConn = null;
            Db.FastImportScope? headScope = null;
            if (fastUnsafeNoFkCheck)
                (headConn, headScope) = await db.BeginFastImportSessionAsync(ct);
            try
            {
                foreach (var ze in entries.Where(IsUserMeta))
                {
                    await TryImportUserAsync(ze, allByPath, results, preserveExisting, headConn, ct);
                    // Register the user as known so the per-entry owner
                    // validator can later check incoming owner_shortname
                    // refs without a PG round-trip. The shortname is the
                    // segment between ".dm/" and "/meta.user.json".
                    if (validation is not null)
                    {
                        var fn = ze.FullName;
                        const string head = "/users/.dm/";
                        const string tail = "/meta.user.json";
                        var h = fn.IndexOf(head, StringComparison.Ordinal);
                        var t = fn.LastIndexOf(tail, StringComparison.Ordinal);
                        if (h >= 0 && t > h + head.Length)
                            validation.RegisterKnownUser(fn[(h + head.Length)..t]);
                    }
                }
                foreach (var ze in entries.Where(z => z.FullName.EndsWith("/.dm/meta.space.json", StringComparison.Ordinal)))
                    await TryImportSpaceAsync(ze, results, preserveExisting, headConn, ct);
                foreach (var ze in entries.Where(IsRoleMeta))
                    await TryImportRoleAsync(ze, results, preserveExisting, headConn, ct);
                foreach (var ze in entries.Where(IsPermissionMeta))
                    await TryImportPermissionAsync(ze, results, preserveExisting, headConn, ct);
                headScope?.MarkSuccess();
            }
            finally
            {
                if (headScope is not null) await headScope.DisposeAsync();
            }
            // Mark head done AFTER the scope disposes (commit landed).
            checkpoint?.MarkHeadDone();
        }

        // ---- Pass 3-5: Entries, Attachments, Histories ----
        // Decide between parallel-per-space and serial-on-one-session. The
        // parallel branch requires --fast (workers need their own
        // session_replication_role='replica' sessions) AND fastParallelism>1
        // AND more than one space worth of work (no point spinning up
        // workers for a single space — same elapsed time, more code paths).
        var tailEntries = entries.Where(IsTailEntry).ToList();
        var spaceGroups = tailEntries
            .GroupBy(GetSpaceName, StringComparer.Ordinal)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToList();

        // Resume: drop space groups the checkpoint says are already done.
        // Only meaningful in the parallel branch below — the serial branch
        // shares one transaction so partial tail commits aren't a thing
        // (a crash mid-serial-tail rolls back the whole pass).
        if (checkpoint is not null)
        {
            var before = spaceGroups.Count;
            spaceGroups = spaceGroups.Where(g => !checkpoint.IsTailDone(g.Key!)).ToList();
            if (before > spaceGroups.Count)
                log.LogInformation("import: --resume skipping {Skipped} of {Total} space tails (already in checkpoint)",
                    before - spaceGroups.Count, before);
        }

        if (fastUnsafeNoFkCheck && workers > 1 && spaceGroups.Count > 1)
        {
            // Prefetch is for zip sources only. ZipArchive isn't thread-safe
            // — workers can't concurrently call ZipArchiveEntry.Open() on
            // entries from the shared archive ("local file header is corrupt"
            // otherwise), so we pre-read every tail entry's bytes once on the
            // main thread and hand workers MemoryStream views via OpenEntry.
            // Filesystem sources skip this entirely: File.OpenRead is
            // thread-safe and prefetching would needlessly double-buffer
            // O(body bytes) of memory for a folder import. OpenEntry falls
            // back to ze.Open() when the prefetch cache is unset.
            if (sourceKind == ImportSourceKind.Zip)
            {
                var prefetched = new Dictionary<string, byte[]>(tailEntries.Count, StringComparer.Ordinal);
                foreach (var ze in tailEntries)
                {
                    await using var s = ze.Open();
                    using var mem = new MemoryStream();
                    await s.CopyToAsync(mem, ct);
                    prefetched[ze.FullName] = mem.ToArray();
                }
                _prefetchedBodies.Value = prefetched;
            }

            // Parallel per space. Each worker owns its own
            // BeginFastImportSessionAsync — its own connection, own
            // session_replication_role flip, own transaction. Workers commit
            // independently; on a worker's throw, only its space rolls back.
            // ImportStats mutations use the Interlocked / lock-protected
            // methods so concurrent updates don't race.
            try
            {
                await Parallel.ForEachAsync(
                    spaceGroups,
                    new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                    async (group, ictx) =>
                    {
                        await ImportSpaceTailAsync(group.ToList(), preserveExisting, results, batchSize, validation, importTags, ictx);
                        // Per-space tail commit landed. Record it so a
                        // future --resume run skips this space entirely.
                        // The MarkTailDone call is lock-protected and
                        // atomically rewrites the sidecar JSON.
                        checkpoint?.MarkTailDone(group.Key!);
                    });
            }
            finally
            {
                _prefetchedBodies.Value = null;
            }
        }
        else
        {
            // Serial fallback. For --fast we still open a single dedicated
            // scope for the tail passes (so bulk COPY has a session to run
            // on and so the deferred cache invalidate at the end still works
            // off committed data). For non-fast there's no scope — repos
            // open their own per-call connections, identical to historical
            // behaviour.
            NpgsqlConnection? tailConn = null;
            Db.FastImportScope? tailScope = null;
            if (fastUnsafeNoFkCheck)
                (tailConn, tailScope) = await db.BeginFastImportSessionAsync(ct);
            try
            {
                await RunTailPassesAsync(tailConn, tailEntries, preserveExisting, results, batchSize, validation, importTags, ct);
                tailScope?.MarkSuccess();
            }
            finally
            {
                if (tailScope is not null) await tailScope.DisposeAsync();
            }
        }

        // Fast mode deferred the per-role/per-permission cache invalidations
        // so the import didn't hit `DELETE FROM userpermissionscache` once
        // per row. Fire one final invalidate now if anything authz-relevant
        // landed. Uses its own fresh connection so it stays out of any
        // remaining transaction.
        if (fastUnsafeNoFkCheck
            && (results.RolesInserted > 0 || results.PermissionsInserted > 0))
        {
            await access.InvalidateAllCachesAsync(ct);
        }

        return Response.Ok(attributes: new()
        {
            ["entries_inserted"] = results.EntriesInserted,
            ["attachments_inserted"] = results.AttachmentsInserted,
            ["spaces_inserted"] = results.SpacesInserted,
            ["users_inserted"] = results.UsersInserted,
            ["roles_inserted"] = results.RolesInserted,
            ["permissions_inserted"] = results.PermissionsInserted,
            ["histories_inserted"] = results.HistoriesInserted,
            ["skipped"] = results.Skipped,
            ["failed_count"] = results.Failed.Count,
            ["failed"] = results.Failed,
        });
    }

    // ---- layout classifiers ----

    private static bool IsUserMeta(ImportEntryRef ze)       => MatchDotDmMeta(ze, "users", "user");
    private static bool IsRoleMeta(ImportEntryRef ze)       => MatchDotDmMeta(ze, "roles", "role");
    private static bool IsPermissionMeta(ImportEntryRef ze) => MatchDotDmMeta(ze, "permissions", "permission");
    private static bool MatchDotDmMeta(ImportEntryRef ze, string parent, string metaType)
        => ze.FullName.Contains($"/{parent}/.dm/", StringComparison.Ordinal)
           && ze.Name == $"meta.{metaType}.json";

    private static bool IsEntryMeta(ImportEntryRef ze)
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

    private static bool IsAttachmentMeta(ImportEntryRef ze)
        => ze.FullName.Contains("/attachments.", StringComparison.Ordinal)
           && ze.Name.StartsWith("meta.", StringComparison.Ordinal)
           && ze.Name.EndsWith(".json", StringComparison.Ordinal);

    // True for entries that belong to the tail passes (Pass 3 entries,
    // Pass 4 attachments, Pass 5 histories) — i.e. everything that is NOT a
    // Pass-1 user meta or a Pass-2 space/role/permission meta. Body files
    // and attachment body files count as tail because the tail passes
    // re-inline them.
    private static bool IsTailEntry(ImportEntryRef ze)
        => !IsUserMeta(ze)
           && !ze.FullName.EndsWith("/.dm/meta.space.json", StringComparison.Ordinal)
           && !IsRoleMeta(ze)
           && !IsPermissionMeta(ze);

    // Extracts the space-root segment (the first path segment) from an
    // entry path. The import already hard-fails the layout check above on
    // any entry that doesn't sit under a `{space}/...` root, so this is
    // total — but defend against an empty string just in case the upstream
    // filter changes.
    private static string GetSpaceName(ImportEntryRef ze)
    {
        var first = ze.FullName.IndexOf('/');
        return first <= 0 ? "" : ze.FullName[..first];
    }

    // Per-space worker for Round 3 parallelism. Owns its own
    // BeginFastImportSessionAsync scope (own connection, own
    // session_replication_role flip, own transaction). On a throw, only
    // this worker's transaction rolls back — sibling workers committing in
    // parallel are unaffected.
    private async Task ImportSpaceTailAsync(
        List<ImportEntryRef> spaceEntries,
        bool preserveExisting,
        ImportStats results,
        int batchSize,
        ImportValidationContext? validation,
        IReadOnlyList<string>? importTags,
        CancellationToken ct)
    {
        var (conn, scope) = await db.BeginFastImportSessionAsync(ct);
        try
        {
            await RunTailPassesAsync(conn, spaceEntries, preserveExisting, results, batchSize, validation, importTags, ct);
            scope.MarkSuccess();
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    // Runs Pass 3 (entries), Pass 4 (attachments), Pass 5 (histories) for a
    // given entry slice on a given connection. The connection is
    // optional: when null we're on the slow (non-fast) path and the repos
    // each open their own connection per call. When non-null we're inside a
    // fast scope (single-session, replica mode) and bulk COPY kicks in.
    //
    // Used by BOTH the serial fallback (one slice = all tail entries on one
    // session) AND the per-space parallel worker (one slice = one space's
    // tail entries on its own session).
    private async Task RunTailPassesAsync(
        NpgsqlConnection? conn,
        IReadOnlyList<ImportEntryRef> zes,
        bool preserveExisting,
        ImportStats results,
        int batchSize,
        ImportValidationContext? validation,
        IReadOnlyList<string>? importTags,
        CancellationToken ct)
    {
        // ---- Pass 3: Entries (including folders) ----
        var bodyLookup = new Dictionary<string, ImportEntryRef>(StringComparer.Ordinal);
        foreach (var ze in zes)
            if (!ze.FullName.Contains("/.dm/", StringComparison.Ordinal))
                bodyLookup[ze.FullName] = ze;

        // Bulk COPY mode kicks in when we have a fast-import connection in
        // hand. Without one (slow path) the per-row Upsert path is used.
        //
        // The bulkEntries accumulator is flushed every `batchSize` rows
        // rather than once at the end — without this, a single space with
        // millions of entries holds every parsed Entry (with inlined JSON
        // payload bodies) in RAM until the loop completes. At ~10 KB avg
        // payload and 10k batch size, peak per-batch is ~100 MB; the full
        // import is bounded regardless of total entry count.
        var bulkEntries = conn is not null ? new List<Entry>(batchSize) : null;
        foreach (var ze in zes.Where(IsEntryMeta))
        {
            await TryImportEntryAsync(ze, bodyLookup, results, preserveExisting, conn, bulkEntries, validation, importTags, ct);
            if (bulkEntries is { Count: > 0 } && bulkEntries.Count >= batchSize)
            {
                await BulkInsertEntriesAsync(conn!, bulkEntries, preserveExisting, results, ct);
                bulkEntries.Clear();
            }
        }
        if (bulkEntries is { Count: > 0 })
            await BulkInsertEntriesAsync(conn!, bulkEntries, preserveExisting, results, ct);

        // ---- Pass 4: Attachments ----
        var attachmentBodies = new Dictionary<string, ImportEntryRef>(StringComparer.Ordinal);
        foreach (var ze in zes)
            if (ze.FullName.Contains("/attachments.", StringComparison.Ordinal) && !ze.Name.StartsWith("meta.", StringComparison.Ordinal))
                attachmentBodies[ze.FullName] = ze;

        var bulkAttachments = conn is not null ? new List<Attachment>(batchSize) : null;
        foreach (var ze in zes.Where(IsAttachmentMeta))
        {
            await TryImportAttachmentAsync(ze, attachmentBodies, results, preserveExisting, conn, bulkAttachments, ct);
            if (bulkAttachments is { Count: > 0 } && bulkAttachments.Count >= batchSize)
            {
                await BulkInsertAttachmentsAsync(conn!, bulkAttachments, preserveExisting, results, ct);
                bulkAttachments.Clear();
            }
        }
        if (bulkAttachments is { Count: > 0 })
            await BulkInsertAttachmentsAsync(conn!, bulkAttachments, preserveExisting, results, ct);

        // ---- Pass 5: Histories ----
        foreach (var ze in zes.Where(z => z.Name == "history.jsonl"))
            await TryImportHistoryAsync(ze, results, conn, ct);
    }

    // ---- importers ----

    // Set owner_shortname to "dmart" when the imported meta omits it (or
    // carries an empty string). "dmart" is guaranteed to exist because
    // AdminBootstrap creates it on every server start. Well-formed exports
    // always populate the field; this is the safety net for malformed ones.
    private static void EnsureOwner(JsonObject node)
    {
        var current = node["owner_shortname"]?.GetValue<string>();
        if (string.IsNullOrEmpty(current))
            node["owner_shortname"] = "dmart";
    }

    private async Task TryImportSpaceAsync(ImportEntryRef ze, ImportStats st, bool preserveExisting, NpgsqlConnection? conn, CancellationToken ct)
    {
        try
        {
            var (spaceName, _) = SplitSpaceAndRest(ze.FullName);
            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["shortname"] ??= spaceName;
            node["subpath"] = "/";
            EnsureOwner(node);
            StripNullChars(node);   // PG jsonb can't store   (22P05)
            var space = node.Deserialize(DmartJsonContext.Default.Space);
            if (space is null) { st.AddFailure(new() { ["path"] = ze.FullName, ["error"] = "empty space meta" }); return; }
            if (preserveExisting && await (conn is null ? spaces.GetAsync(space.Shortname, ct) : spaces.GetAsync(space.Shortname, conn, ct)) is not null)
            {
                st.IncSkipped();
                return;
            }
            if (conn is null) await spaces.UpsertAsync(space, ct);
            else              await spaces.UpsertAsync(space, conn, ct);
            st.IncSpaces();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import space failed at {Path}", ze.FullName);
            st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "space", ["error"] = ex.Message });
        }
    }

    private async Task TryImportUserAsync(ImportEntryRef ze, IReadOnlyDictionary<string, ImportEntryRef> allByPath, ImportStats st, bool preserveExisting, NpgsqlConnection? conn, CancellationToken ct)
    {
        try
        {
            var (spaceName, _) = SplitSpaceAndRest(ze.FullName);
            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = "/users";
            EnsureOwner(node);
            await InlinePayloadBodyAsync(node, $"{spaceName}/users", allByPath, ct);
            StripNullChars(node);   // PG jsonb can't store   (22P05)
            var user = node.Deserialize(DmartJsonContext.Default.User);
            if (user is null) { st.AddFailure(new() { ["path"] = ze.FullName, ["error"] = "empty user meta" }); return; }
            if (preserveExisting && await (conn is null ? users.GetByShortnameAsync(user.Shortname, ct) : users.GetByShortnameAsync(user.Shortname, conn, ct)) is not null)
            {
                st.IncSkipped();
                return;
            }
            if (conn is null) await users.UpsertAsync(user, ct);
            else              await users.UpsertAsync(user, conn, ct);
            st.IncUsers();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import user failed at {Path}", ze.FullName);
            st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "user", ["error"] = ex.Message });
        }
    }

    private async Task TryImportRoleAsync(ImportEntryRef ze, ImportStats st, bool preserveExisting, NpgsqlConnection? conn, CancellationToken ct)
    {
        try
        {
            var (spaceName, _) = SplitSpaceAndRest(ze.FullName);
            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = "/roles";
            EnsureOwner(node);
            StripNullChars(node);   // PG jsonb can't store   (22P05)
            var role = node.Deserialize(DmartJsonContext.Default.Role);
            if (role is null) { st.AddFailure(new() { ["path"] = ze.FullName, ["error"] = "empty role meta" }); return; }
            if (preserveExisting && await (conn is null ? access.GetRoleAsync(role.Shortname, ct) : access.GetRoleAsync(role.Shortname, conn, ct)) is not null)
            {
                st.IncSkipped();
                return;
            }
            if (conn is null) await access.UpsertRoleAsync(role, ct);
            else              await access.UpsertRoleAsync(role, conn, deferCacheRefresh: true, ct);
            st.IncRoles();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import role failed at {Path}", ze.FullName);
            st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "role", ["error"] = ex.Message });
        }
    }

    private async Task TryImportPermissionAsync(ImportEntryRef ze, ImportStats st, bool preserveExisting, NpgsqlConnection? conn, CancellationToken ct)
    {
        try
        {
            var (spaceName, _) = SplitSpaceAndRest(ze.FullName);
            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = "/permissions";
            EnsureOwner(node);
            StripNullChars(node);   // PG jsonb can't store   (22P05)
            var perm = node.Deserialize(DmartJsonContext.Default.Permission);
            if (perm is null) { st.AddFailure(new() { ["path"] = ze.FullName, ["error"] = "empty permission meta" }); return; }
            if (preserveExisting && await (conn is null ? access.GetPermissionAsync(perm.Shortname, ct) : access.GetPermissionAsync(perm.Shortname, conn, ct)) is not null)
            {
                st.IncSkipped();
                return;
            }
            if (conn is null) await access.UpsertPermissionAsync(perm, ct);
            else              await access.UpsertPermissionAsync(perm, conn, deferCacheRefresh: true, ct);
            st.IncPermissions();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import permission failed at {Path}", ze.FullName);
            st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "permission", ["error"] = ex.Message });
        }
    }

    // Path → (subpath, shortname) decoder for entry/folder metas. Extracted
    // and made `internal` so unit tests can pin the nesting math without a
    // live DB. The on-disk path is the source of truth for both fields —
    // any drift in the meta JSON (renames, manual edits) is corrected to
    // the path on import.
    internal static (string Subpath, string Shortname) DecodeEntryPath(string fullPath)
    {
        var (_, rest) = SplitSpaceAndRest(fullPath);
        var name = fullPath[(fullPath.LastIndexOf('/') + 1)..];
        if (name == "meta.folder.json")
        {
            var dmIdx = rest.LastIndexOf("/.dm/meta.folder.json", StringComparison.Ordinal);
            if (dmIdx < 0) throw new InvalidDataException("folder meta path malformed");
            var withFolder = rest[..dmIdx];
            var lastSlash = withFolder.LastIndexOf('/');
            var subpath = lastSlash < 0 ? "/" : "/" + withFolder[..lastSlash];
            var shortname = lastSlash < 0 ? withFolder : withFolder[(lastSlash + 1)..];
            return (subpath, shortname);
        }
        string subp;
        string afterDm;
        if (rest.StartsWith(".dm/", StringComparison.Ordinal))
        {
            subp = "/";
            afterDm = rest[".dm/".Length..];
        }
        else
        {
            var idx = rest.IndexOf("/.dm/", StringComparison.Ordinal);
            if (idx < 0) throw new InvalidDataException("entry meta path missing /.dm/");
            subp = "/" + rest[..idx];
            afterDm = rest[(idx + "/.dm/".Length)..];
        }
        var slashBeforeMeta = afterDm.IndexOf("/meta.", StringComparison.Ordinal);
        if (slashBeforeMeta < 0) throw new InvalidDataException("entry meta path malformed");
        return (subp, afterDm[..slashBeforeMeta]);
    }

    // Filename → shortname for attachment metas. Extracted and made `internal`
    // so unit tests can pin the slicing without spinning up an import. Mirrors
    // DecodeEntryPath's policy: the on-disk filename is the source of truth
    // for the attachment's shortname; the meta JSON's stored shortname is
    // overwritten on import to match.
    internal static string DecodeAttachmentShortname(string fname)
    {
        const string metaPrefix = "meta.";
        const string metaSuffix = ".json";
        if (!fname.StartsWith(metaPrefix, StringComparison.Ordinal) || !fname.EndsWith(metaSuffix, StringComparison.Ordinal))
            throw new InvalidDataException("attachment meta filename malformed: expected 'meta.{shortname}.json'");
        return fname[metaPrefix.Length..^metaSuffix.Length];
    }

    private async Task TryImportEntryAsync(
        ImportEntryRef ze, Dictionary<string, ImportEntryRef> bodyLookup,
        ImportStats st, bool preserveExisting, NpgsqlConnection? conn,
        List<Entry>? bulkCollect, ImportValidationContext? validation,
        IReadOnlyList<string>? importTags, CancellationToken ct)
    {
        try
        {
            var (spaceName, rest) = SplitSpaceAndRest(ze.FullName);
            var isFolder = ze.Name == "meta.folder.json";
            // The on-disk path is the source of truth for both subpath AND
            // shortname — if the meta JSON's shortname disagrees (manual
            // edits, partial overlay, a stale meta from a previous shortname),
            // the path wins. This matches what dmart-on-disk has always
            // meant: the directory IS the entry.
            var (subpath, shortname) = DecodeEntryPath(ze.FullName);

            System.Text.Json.Nodes.JsonObject node;
            try
            {
                node = await ReadJsonObjectAsync(ze, ct);
            }
            catch (Exception parseEx) when (validation is not null)
            {
                // Validation-enabled mode: a malformed meta isn't a silent
                // failure. Log it to the sidecar and skip the entry instead
                // of letting the import barf with a generic error.
                validation.Sink.Add(new ImportIssue
                {
                    Path = ze.FullName,
                    Kind = "parse-error",
                    Action = "skipped",
                    Details = new() { ["error"] = parseEx.Message },
                });
                st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "parse-error", ["error"] = parseEx.Message });
                return;
            }
            node["space_name"] = spaceName;
            node["subpath"] = subpath;
            node["shortname"] = shortname;
            node["resource_type"] ??= InferResourceTypeFromFilename(ze.Name);

            // ---- --tag: stamp arbitrary tags onto every imported entry ----
            // Applied regardless of --no-validate. Deduped against existing
            // tags so re-runs don't pile up duplicates. Common use: mark a
            // migration batch (e.g. "migrated-2026-05") so the imported rows
            // can be identified / filtered / rolled back later.
            if (importTags is { Count: > 0 })
            {
                if (node["tags"] is not JsonArray tagsArr)
                {
                    tagsArr = new JsonArray();
                    node["tags"] = tagsArr;
                }
                var existing = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in tagsArr)
                    if (t is JsonValue tv && tv.TryGetValue<string>(out var s))
                        existing.Add(s);
                foreach (var tag in importTags)
                    if (existing.Add(tag))
                        tagsArr.Add((JsonNode)tag);   // implicit string→JsonNode; AOT-safe (avoids generic Add<T>)
            }

            // ============================================================
            // VALIDATION HOOKS — only when validation is enabled.
            // Source files are NEVER mutated; all fixes happen on `node`
            // before it deserializes into the Entry instance below.
            // ============================================================
            if (validation is not null)
            {
                // ---- UUID dedup ----
                // First reader of any given uuid keeps it; later collisions
                // get a fresh Guid. Covers both source-internal duplicates
                // and source-vs-PG duplicates (the PG check is implicit:
                // the original uuid would have caused a PK violation; the
                // regen turns it into a fresh row whose composite
                // (space, subpath, shortname) is what idempotency keys on).
                var uuidStr = node["uuid"]?.GetValue<string?>();
                if (!string.IsNullOrEmpty(uuidStr) && Guid.TryParse(uuidStr, out var parsedUuid))
                {
                    if (!validation.TryClaimUuid(parsedUuid, ze.FullName))
                    {
                        var newUuid = Guid.NewGuid();
                        validation.RegisterClaimedUuid(newUuid, ze.FullName);
                        validation.Sink.Add(new ImportIssue
                        {
                            Path = ze.FullName,
                            Kind = "uuid-regenerated",
                            Action = "fixed-in-memory",
                            Details = new()
                            {
                                ["original_uuid"] = uuidStr!,
                                ["new_uuid"] = newUuid.ToString(),
                                ["first_seen_at"] = validation.GetUuidOwner(parsedUuid) ?? "(unknown)",
                            },
                        });
                        node["uuid"] = newUuid.ToString();
                    }
                }

                // ---- Owner remap ----
                // Unknown owner → swap to "dmart" sentinel in memory.
                // The known-users set was seeded from PG + populated from
                // source-side meta.user.json files during the head pass.
                var ownerVal = node["owner_shortname"]?.GetValue<string?>();
                if (!string.IsNullOrEmpty(ownerVal) && !validation.IsKnownOwner(ownerVal))
                {
                    validation.Sink.Add(new ImportIssue
                    {
                        Path = ze.FullName,
                        Kind = "owner-remapped",
                        Action = "fixed-in-memory",
                        Details = new()
                        {
                            ["shortname"] = shortname,
                            ["original_owner"] = ownerVal!,
                            ["replacement"] = "dmart",
                        },
                    });
                    node["owner_shortname"] = "dmart";
                }

                // ---- Schema validation moved BELOW InlinePayloadBody ----
                // The body must be in memory before we can check it. See the
                // matching block after InlinePayloadBody() runs.
            }

            // Re-inline payload.body from the external file when it's a string
            // filename that points at a sibling JSON/HTML/text/image file.
            //   non-folder: body lives at "{space}/{subpath}/{sn}.{ext}"
            //   folder:     body lives next to the folder directory, i.e. one
            //               level ABOVE the folder's own dir
            string baseDir;
            if (isFolder)
            {
                var cutAt = rest.LastIndexOf("/.dm/meta.folder.json", StringComparison.Ordinal);
                var withFolder = rest[..cutAt];                 // "{subpath}/{folder_sn}"
                var lastSlash = withFolder.LastIndexOf('/');
                var parentDir = lastSlash < 0 ? "" : withFolder[..lastSlash];
                baseDir = string.IsNullOrEmpty(parentDir) ? spaceName : $"{spaceName}/{parentDir}";
            }
            else
            {
                baseDir = $"{spaceName}/{subpath.TrimStart('/')}".TrimEnd('/');
            }
            InlinePayloadBody(node, baseDir, bodyLookup);
            EnsureOwner(node);

            // ---- Strip PG-incompatible NUL chars ( ) ----
            // Unconditional — runs even with --no-validate — because it's a
            // correctness fix for PG jsonb (22P05), not a validation choice.
            // Covers both meta fields and the just-inlined body. Logged to
            // the sidecar only when validation is on.
            var nullsStripped = StripNullChars(node);
            if (nullsStripped > 0 && validation is not null)
            {
                validation.Sink.Add(new ImportIssue
                {
                    Path = ze.FullName,
                    Kind = "null-bytes-stripped",
                    Action = "fixed-in-memory",
                    Details = new() { ["strings_modified"] = nullsStripped },
                });
            }

            // ---- Schema validation (runs AFTER body has been inlined) ----
            // PG always stores body inlined into payload.body — never as a
            // filename ref. So validation must happen with the actual body
            // bytes in hand, which is the moment between InlinePayloadBody
            // (loads the body off disk) and the Deserialize below (turns
            // node into the Entry that goes into PG).
            //
            // Three body shapes can land here:
            //   * JSON object/array  — produced by inlining a .json body file
            //                          (or already inline in source meta)
            //   * String             — inlining failed (body file missing on
            //                          disk), or the original source had a
            //                          filename ref that we couldn't resolve.
            //                          Treat as inline JSON if it parses;
            //                          otherwise skip validation for this row.
            //   * null               — no body at all. Nothing to validate.
            if (validation is not null)
            {
                var schemaShortname = node["payload"]?["schema_shortname"]?.GetValue<string?>();
                if (!string.IsNullOrEmpty(schemaShortname))
                {
                    var bodyNode = node["payload"]?["body"];
                    JsonElement? bodyEl = null;
                    if (bodyNode is System.Text.Json.Nodes.JsonObject || bodyNode is System.Text.Json.Nodes.JsonArray)
                    {
                        using var d = JsonDocument.Parse(bodyNode.ToJsonString());
                        bodyEl = d.RootElement.Clone();
                    }
                    else if (bodyNode is System.Text.Json.Nodes.JsonValue v
                        && v.TryGetValue<string>(out var bodyStr)
                        && !string.IsNullOrEmpty(bodyStr))
                    {
                        // String body — could be inlined text/HTML/etc that
                        // happens to look like JSON, or a leftover filename
                        // ref. Try to parse; on failure skip validation for
                        // this row (we still log the unresolved-body case as
                        // a separate issue so the operator sees it).
                        try
                        {
                            using var d = JsonDocument.Parse(bodyStr);
                            bodyEl = d.RootElement.Clone();
                        }
                        catch
                        {
                            validation.Sink.Add(new ImportIssue
                            {
                                Path = ze.FullName,
                                Kind = "body-not-validatable",
                                Action = "skipped-validation",
                                Details = new()
                                {
                                    ["reason"] = "body is a non-JSON string (likely an unresolved filename or text content)",
                                    ["schema_shortname"] = schemaShortname!,
                                },
                            });
                        }
                    }

                    if (bodyEl is not null)
                    {
                        var errors = await validation.ValidateBodyAsync(
                            spaceName, schemaShortname!, bodyEl.Value, ct);
                        if (errors is not null)
                        {
                            validation.Sink.Add(new ImportIssue
                            {
                                Path = ze.FullName,
                                Kind = "schema-violation",
                                Action = "skipped",
                                Details = new()
                                {
                                    ["schema_shortname"] = schemaShortname!,
                                    ["space"] = spaceName,
                                    ["errors"] = errors,
                                },
                            });
                            st.AddFailure(new()
                            {
                                ["path"] = ze.FullName,
                                ["kind"] = "schema-violation",
                                ["error"] = string.Join("; ", errors),
                            });
                            return;  // SKIP — don't insert into PG
                        }
                    }
                }
            }

            var entry = node.Deserialize(DmartJsonContext.Default.Entry);
            if (entry is null) { st.AddFailure(new() { ["path"] = ze.FullName, ["error"] = "empty entry meta" }); return; }

            // Import goes through the repo (not EntryService.CreateAsync) so
            // plugin hooks don't fire per-row and perm checks don't block a
            // bulk restore. This mirrors Python's bulk_insert_in_batches path.
            // Fast-mode bulk path: collect into the caller's list; the bulk
            // helper handles preserveExisting via ON CONFLICT DO NOTHING and
            // updates st.EntriesInserted / st.Skipped from row counts after
            // the merge.
            if (bulkCollect is not null)
            {
                bulkCollect.Add(entry);
                return;
            }
            if (preserveExisting && await (conn is null
                    ? entries.GetAsync(entry.SpaceName, entry.Subpath, entry.Shortname, entry.ResourceType, ct)
                    : entries.GetAsync(entry.SpaceName, entry.Subpath, entry.Shortname, entry.ResourceType, conn, ct)) is not null)
            {
                st.IncSkipped();
                return;
            }
            if (conn is null) await entries.UpsertAsync(entry, ct);
            else              await entries.UpsertAsync(entry, conn, ct);
            st.IncEntries();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import entry failed at {Path}", ze.FullName);
            st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "entry", ["error"] = ex.Message });
        }
    }

    private async Task TryImportAttachmentAsync(
        ImportEntryRef ze, Dictionary<string, ImportEntryRef> bodies,
        ImportStats st, bool preserveExisting, NpgsqlConnection? conn,
        List<Attachment>? bulkCollect, CancellationToken ct)
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

            // Attachment shortname comes from the meta filename: "meta.{att_sn}.json".
            // Path-derived to match the entry-import behaviour: the on-disk
            // filename is authoritative if the meta JSON's shortname has
            // drifted (rename, overlay, manual edit).
            var attShortname = DecodeAttachmentShortname(ze.Name);

            var node = await ReadJsonObjectAsync(ze, ct);
            node["space_name"] = spaceName;
            node["subpath"] = attSubpath;
            node["shortname"] = attShortname;
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
                            await using var bs = OpenEntry(bodyZe);
                            using var sr = new StreamReader(bs);
                            var jsonText = await sr.ReadToEndAsync(ct);
                            p["body"] = JsonNode.Parse(jsonText);
                        }
                        else
                        {
                            await using var bs = OpenEntry(bodyZe);
                            using var mem = new MemoryStream();
                            await bs.CopyToAsync(mem, ct);
                            node["media"] = Convert.ToBase64String(mem.ToArray());
                        }
                    }
                }
            }

            EnsureOwner(node);
            StripNullChars(node);   // PG jsonb can't store   (22P05)
            var att = node.Deserialize(DmartJsonContext.Default.Attachment);
            if (att is null) { st.AddFailure(new() { ["path"] = ze.FullName, ["error"] = "empty attachment meta" }); return; }
            // Fast-mode bulk path — see TryImportEntryAsync for the same pattern.
            if (bulkCollect is not null)
            {
                bulkCollect.Add(att);
                return;
            }
            if (preserveExisting && await (conn is null
                    ? attachments.GetAsync(att.SpaceName, att.Subpath, att.Shortname, ct)
                    : attachments.GetAsync(att.SpaceName, att.Subpath, att.Shortname, conn, ct)) is not null)
            {
                st.IncSkipped();
                return;
            }
            if (conn is null) await attachments.UpsertAsync(att, ct);
            else              await attachments.UpsertAsync(att, conn, ct);
            st.IncAttachments();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import attachment failed at {Path}", ze.FullName);
            st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "attachment", ["error"] = ex.Message });
        }
    }

    private async Task TryImportHistoryAsync(ImportEntryRef ze, ImportStats st, NpgsqlConnection? conn, CancellationToken ct)
    {
        try
        {
            var (spaceName, rest) = SplitSpaceAndRest(ze.FullName);
            // history path: "{space}/{subpath}/.dm/{sn}/history.jsonl", or
            // "{space}/.dm/{sn}/history.jsonl" when the entry sits at subpath "/".
            // Handle both shapes — an entry at root has no `/.dm/` separator,
            // `rest` starts with `.dm/` directly.
            string subpath;
            string afterDm;
            if (rest.StartsWith(".dm/", StringComparison.Ordinal))
            {
                subpath = "/";
                afterDm = rest[".dm/".Length..];
            }
            else
            {
                var idx = rest.IndexOf("/.dm/", StringComparison.Ordinal);
                if (idx < 0) throw new InvalidDataException("history path missing /.dm/");
                subpath = "/" + rest[..idx];
                afterDm = rest[(idx + "/.dm/".Length)..];
            }
            var sn = afterDm.Split('/')[0];

            await using var stream = OpenEntry(ze);
            using var sr = new StreamReader(stream);
            string? line;
            while ((line = await sr.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var hNode = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("not a JSON object");
                    StripNullChars(hNode);   // PG jsonb can't store   (22P05) — common in history diffs
                    var owner = hNode["owner_shortname"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(owner)) owner = "dmart";
                    // History Append takes Dictionary<string, object>. Deserialize
                    // via the source-gen context so AOT trimming is happy.
                    Dictionary<string, object>? diff = null;
                    if (hNode["diff"] is JsonObject d)
                        diff = d.Deserialize(DmartJsonContext.Default.DictionaryStringObject);
                    Dictionary<string, object>? reqHeaders = null;
                    if (hNode["request_headers"] is JsonObject rh)
                        reqHeaders = rh.Deserialize(DmartJsonContext.Default.DictionaryStringObject);
                    if (conn is null)
                        await histories.AppendAsync(spaceName, subpath, sn, owner, reqHeaders, diff, ct);
                    else
                        await histories.AppendAsync(spaceName, subpath, sn, owner, reqHeaders, diff, conn, ct);
                    st.IncHistories();
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "history line skipped in {Path}", ze.FullName);
                    st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "history_line", ["error"] = ex.Message });
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "import history failed at {Path}", ze.FullName);
            st.AddFailure(new() { ["path"] = ze.FullName, ["kind"] = "history", ["error"] = ex.Message });
        }
    }

    // ========================================================================
    // Bulk inserters — used only by the `--fast` import path. Replace the
    // per-row `INSERT ... ON CONFLICT` loop with a single binary COPY into a
    // temp table followed by one `INSERT ... SELECT ... ON CONFLICT` merge.
    // For the two high-cardinality tables (entries, attachments) this
    // collapses N protocol round-trips into roughly two, which is where the
    // order-of-magnitude speedup in `--fast` actually lives.
    //
    // SQL parity contract: the column list AND the per-row WriteAsync calls
    // below MUST stay in lockstep with each other and with the per-row
    // UpsertAsync in EntryRepository / AttachmentRepository — adding or
    // reordering a column requires three coordinated edits. The merge clause
    // (the SET list on ON CONFLICT DO UPDATE) likewise mirrors the per-row
    // upsert's. The repos carry a comment pointing here as a reminder.
    // ========================================================================

    private const string EntryCopyColumns =
        "uuid, shortname, space_name, subpath, is_active, slug, " +
        "displayname, description, tags, created_at, updated_at, " +
        "owner_shortname, owner_group_shortname, acl, payload, relationships, " +
        "last_checksum_history, resource_type, state, is_open, reporter, " +
        "workflow_shortname, collaborators, resolution_reason, query_policies";

    private const string EntryConflictSet = """
        is_active = EXCLUDED.is_active,
        slug = EXCLUDED.slug,
        displayname = EXCLUDED.displayname,
        description = EXCLUDED.description,
        tags = EXCLUDED.tags,
        updated_at = EXCLUDED.updated_at,
        owner_shortname = EXCLUDED.owner_shortname,
        owner_group_shortname = EXCLUDED.owner_group_shortname,
        acl = EXCLUDED.acl,
        payload = EXCLUDED.payload,
        relationships = EXCLUDED.relationships,
        last_checksum_history = EXCLUDED.last_checksum_history,
        resource_type = EXCLUDED.resource_type,
        state = EXCLUDED.state,
        is_open = EXCLUDED.is_open,
        reporter = EXCLUDED.reporter,
        workflow_shortname = EXCLUDED.workflow_shortname,
        collaborators = EXCLUDED.collaborators,
        resolution_reason = EXCLUDED.resolution_reason,
        query_policies = EXCLUDED.query_policies
        """;

    private static async Task BulkInsertEntriesAsync(
        NpgsqlConnection conn, List<Entry> rows, bool preserveExisting,
        ImportStats st, CancellationToken ct)
    {
        // Compute query_policies up front — the per-row UpsertAsync does this
        // inside its own scope; the bulk path must replicate it because the
        // COPY writes the column verbatim from `e.QueryPolicies`.
        var now = TimeUtils.Now();
        for (var i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with { QueryPolicies = Utils.QueryPolicies.Generate(rows[i]) };

        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE _imp_entries (LIKE entries INCLUDING DEFAULTS) ON COMMIT DROP", conn))
            await create.ExecuteNonQueryAsync(ct);

        await using (var writer = await conn.BeginBinaryImportAsync(
            $"COPY _imp_entries ({EntryCopyColumns}) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var e in rows)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(Guid.Parse(e.Uuid), NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(e.Shortname, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.SpaceName, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.Subpath, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.IsActive, NpgsqlDbType.Boolean, ct);
                await WriteNullableTextAsync(writer, e.Slug, ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(e.Displayname), ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(e.Description), ct);
                await writer.WriteAsync(JsonbHelpers.ToJsonbList(e.Tags), NpgsqlDbType.Jsonb, ct);
                await writer.WriteAsync(e.CreatedAt == default ? now : e.CreatedAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(e.UpdatedAt == default ? now : e.UpdatedAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(e.OwnerShortname, NpgsqlDbType.Text, ct);
                await WriteNullableTextAsync(writer, e.OwnerGroupShortname, ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(e.Acl), ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(e.Payload), ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(e.Relationships), ct);
                await WriteNullableTextAsync(writer, e.LastChecksumHistory, ct);
                // resource_type is the PG `resourcetype` enum. Write the literal
                // string and tag it as `Unknown` so Npgsql ships it as raw text
                // and PG's text→enum cast resolves it on the server side.
                await writer.WriteAsync(JsonbHelpers.EnumMember(e.ResourceType), NpgsqlDbType.Unknown, ct);
                await WriteNullableTextAsync(writer, e.State, ct);
                if (e.IsOpen is null) await writer.WriteNullAsync(ct);
                else                  await writer.WriteAsync(e.IsOpen.Value, NpgsqlDbType.Boolean, ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(e.Reporter), ct);
                await WriteNullableTextAsync(writer, e.WorkflowShortname, ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(e.Collaborators), ct);
                await WriteNullableTextAsync(writer, e.ResolutionReason, ct);
                await writer.WriteAsync((e.QueryPolicies ?? new()).ToArray(),
                    NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
            }
            await writer.CompleteAsync(ct);
        }

        var mergeSql = preserveExisting
            ? $"INSERT INTO entries ({EntryCopyColumns}) SELECT {EntryCopyColumns} FROM _imp_entries "
                + "ON CONFLICT (shortname, space_name, subpath) DO NOTHING"
            : $"INSERT INTO entries ({EntryCopyColumns}) SELECT {EntryCopyColumns} FROM _imp_entries "
                + $"ON CONFLICT (shortname, space_name, subpath) DO UPDATE SET {EntryConflictSet}";

        await using (var merge = new NpgsqlCommand(mergeSql, conn))
        {
            var affected = await merge.ExecuteNonQueryAsync(ct);
            // affected = inserted + (updated when not preserveExisting).
            // preserveExisting=true: DO NOTHING means skipped rows aren't in `affected`.
            st.AddEntries(affected);
            if (preserveExisting) st.AddSkipped(rows.Count - affected);
        }

        // The temp table is `ON COMMIT DROP` and we're inside the fast-import
        // transaction — but the same import will reuse this connection for
        // the next bulk pass (attachments) in the same transaction, so the
        // table is still around. Drop explicitly to release the OID and to
        // keep the helpers self-contained (caller-order independent).
        await using (var drop = new NpgsqlCommand("DROP TABLE _imp_entries", conn))
            await drop.ExecuteNonQueryAsync(ct);
    }

    private const string AttachmentCopyColumns =
        "uuid, shortname, space_name, subpath, is_active, slug, " +
        "displayname, description, tags, created_at, updated_at, " +
        "owner_shortname, owner_group_shortname, acl, payload, relationships, " +
        "last_checksum_history, resource_type, media, body, state";

    private const string AttachmentConflictSet = """
        is_active = EXCLUDED.is_active,
        slug = EXCLUDED.slug,
        displayname = EXCLUDED.displayname,
        description = EXCLUDED.description,
        tags = EXCLUDED.tags,
        updated_at = EXCLUDED.updated_at,
        owner_shortname = EXCLUDED.owner_shortname,
        owner_group_shortname = EXCLUDED.owner_group_shortname,
        acl = EXCLUDED.acl,
        payload = EXCLUDED.payload,
        relationships = EXCLUDED.relationships,
        last_checksum_history = EXCLUDED.last_checksum_history,
        resource_type = EXCLUDED.resource_type,
        media = EXCLUDED.media,
        body = EXCLUDED.body,
        state = EXCLUDED.state
        """;

    private static async Task BulkInsertAttachmentsAsync(
        NpgsqlConnection conn, List<Attachment> rows, bool preserveExisting,
        ImportStats st, CancellationToken ct)
    {
        var now = TimeUtils.Now();

        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE _imp_attachments (LIKE attachments INCLUDING DEFAULTS) ON COMMIT DROP", conn))
            await create.ExecuteNonQueryAsync(ct);

        await using (var writer = await conn.BeginBinaryImportAsync(
            $"COPY _imp_attachments ({AttachmentCopyColumns}) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var a in rows)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(Guid.Parse(a.Uuid), NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(a.Shortname, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(a.SpaceName, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(a.Subpath, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(a.IsActive, NpgsqlDbType.Boolean, ct);
                await WriteNullableTextAsync(writer, a.Slug, ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(a.Displayname), ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(a.Description), ct);
                await writer.WriteAsync(JsonbHelpers.ToJsonbList(a.Tags), NpgsqlDbType.Jsonb, ct);
                await writer.WriteAsync(a.CreatedAt == default ? now : a.CreatedAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(a.UpdatedAt == default ? now : a.UpdatedAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(a.OwnerShortname, NpgsqlDbType.Text, ct);
                await WriteNullableTextAsync(writer, a.OwnerGroupShortname, ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(a.Acl), ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(a.Payload), ct);
                await WriteNullableJsonbAsync(writer, JsonbHelpers.ToJsonb(a.Relationships), ct);
                await WriteNullableTextAsync(writer, a.LastChecksumHistory, ct);
                await writer.WriteAsync(JsonbHelpers.EnumMember(a.ResourceType), NpgsqlDbType.Unknown, ct);
                if (a.Media is null) await writer.WriteNullAsync(ct);
                else                 await writer.WriteAsync(a.Media, NpgsqlDbType.Bytea, ct);
                await WriteNullableTextAsync(writer, a.Body, ct);
                await WriteNullableTextAsync(writer, a.State, ct);
            }
            await writer.CompleteAsync(ct);
        }

        var mergeSql = preserveExisting
            ? $"INSERT INTO attachments ({AttachmentCopyColumns}) SELECT {AttachmentCopyColumns} FROM _imp_attachments "
                + "ON CONFLICT (shortname, space_name, subpath) DO NOTHING"
            : $"INSERT INTO attachments ({AttachmentCopyColumns}) SELECT {AttachmentCopyColumns} FROM _imp_attachments "
                + $"ON CONFLICT (shortname, space_name, subpath) DO UPDATE SET {AttachmentConflictSet}";

        await using (var merge = new NpgsqlCommand(mergeSql, conn))
        {
            var affected = await merge.ExecuteNonQueryAsync(ct);
            st.AddAttachments(affected);
            if (preserveExisting) st.AddSkipped(rows.Count - affected);
        }

        await using (var drop = new NpgsqlCommand("DROP TABLE _imp_attachments", conn))
            await drop.ExecuteNonQueryAsync(ct);
    }

    // Small adapters so the per-column WriteAsync loop above stays compact.
    private static Task WriteNullableTextAsync(NpgsqlBinaryImporter w, string? v, CancellationToken ct)
        => v is null ? w.WriteNullAsync(ct) : w.WriteAsync(v, NpgsqlDbType.Text, ct);

    private static Task WriteNullableJsonbAsync(NpgsqlBinaryImporter w, string? json, CancellationToken ct)
        => json is null ? w.WriteNullAsync(ct) : w.WriteAsync(json, NpgsqlDbType.Jsonb, ct);

    // ========================================================================
    // Helpers — JSON transforms + zip IO + path splitting
    // ========================================================================

    private sealed class ImportStats
    {
        // Public for read access at the response-building site (after all
        // workers have joined via Parallel.ForEachAsync, so memory ordering
        // is fine). Mutation MUST go through the methods below to keep
        // round-3's per-space-parallel workers race-free.
        public int EntriesInserted;
        public int AttachmentsInserted;
        public int SpacesInserted;
        public int UsersInserted;
        public int RolesInserted;
        public int PermissionsInserted;
        public int HistoriesInserted;
        // Rows skipped under preserveExisting because a matching row already
        // existed in the DB. Reported back to the caller so they can tell
        // "no-op" runs apart from "everything failed" runs.
        public int Skipped;
        public readonly List<Dictionary<string, object>> Failed = new();

        // Thread-safe counter bumpers — Interlocked is the cheapest way to
        // make `++` race-free across the parallel space workers. The cost
        // (a CMPXCHG-equivalent) is in the nanoseconds, negligible against
        // the actual import work.
        public void IncEntries()      => Interlocked.Increment(ref EntriesInserted);
        public void IncAttachments()  => Interlocked.Increment(ref AttachmentsInserted);
        public void IncSpaces()       => Interlocked.Increment(ref SpacesInserted);
        public void IncUsers()        => Interlocked.Increment(ref UsersInserted);
        public void IncRoles()        => Interlocked.Increment(ref RolesInserted);
        public void IncPermissions()  => Interlocked.Increment(ref PermissionsInserted);
        public void IncHistories()    => Interlocked.Increment(ref HistoriesInserted);
        public void IncSkipped()      => Interlocked.Increment(ref Skipped);
        public void AddEntries(int n)     => Interlocked.Add(ref EntriesInserted, n);
        public void AddAttachments(int n) => Interlocked.Add(ref AttachmentsInserted, n);
        public void AddSkipped(int n)     => Interlocked.Add(ref Skipped, n);
        // List<T>.Add isn't safe under concurrent writers; lock around it.
        // Reads at the response site happen after the parallel join → no
        // lock needed for the final enumeration.
        public void AddFailure(Dictionary<string, object> failure)
        {
            lock (Failed) Failed.Add(failure);
        }
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

    // Text-ish content types whose body file is safe to inline into the
    // jsonb meta. Anything else (image / media / binary blobs) stays as the
    // filename string in payload.body — Postgres jsonb cannot store
    // the bytes that a raw PNG/MP3/PDF would produce when round-tripped
    // through StreamReader, and inlining them gives the user nothing they
    // can use anyway (the body is meant to be fetched from the filesystem).
    //
    // The set must be the union of:
    //   1. What MaybeExternalizePayloadBodyAsync (this file) writes out:
    //        json, html, text, markdown
    //      Round-trip parity for our own export → import path.
    //   2. What dmart Python's exporter is known to externalize for
    //      text-ish bodies it ships: csv, jsonl.
    //      We accept Python-exported zips on the import side, so the
    //      C# importer must know how to re-inline what Python wrote.
    //
    // Deliberately excluded text-ish ContentType members (see
    // Dmart.Models.Enums.ContentType):
    //   comment, reaction — bodies are tiny strings stored inline in
    //                       meta. No exporter externalizes them, so a
    //                       sibling body file never exists; including
    //                       them here would be dead code.
    //   python            — source code; current importer keeps `.py`
    //                       files as on-disk attachments referenced by
    //                       name rather than inlining into jsonb. Add
    //                       here only when MaybeExternalizePayloadBodyAsync
    //                       grows a `case "python"` branch.
    //
    // Invariant: if you add a `case` to MaybeExternalizePayloadBodyAsync's
    // switch, add the matching content_type here too — otherwise the
    // round-trip silently drops the body (file written on export, ignored
    // on import).
    // internal for unit tests in dmart.Tests so the policy (which
    // content types round-trip through the externalize/inline path) can
    // be asserted without spinning up the full DI graph.
    internal static readonly HashSet<string> InlinableContentTypes =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "text", "html", "markdown", "csv", "jsonl",
    };

    // Re-inline the externalized payload.body from a sibling body file.
    // Mutates `metaNode` in place.
    private static void InlinePayloadBody(
        JsonObject metaNode, string baseDir, Dictionary<string, ImportEntryRef> bodies)
    {
        if (metaNode["payload"] is not JsonObject payload) return;
        if (payload["body"] is not JsonValue bodyVal) return;
        if (!bodyVal.TryGetValue<string>(out var filename) || string.IsNullOrEmpty(filename)) return;
        var contentType = (payload["content_type"]?.GetValue<string>() ?? "").ToLowerInvariant();
        if (!InlinableContentTypes.Contains(contentType)) return;
        // Python stores the filename relative to the entry's subpath directory.
        var bodyPath = $"{baseDir}/{filename}".Replace("//", "/");
        if (!bodies.TryGetValue(bodyPath, out var ze)) return;
        using var s = OpenEntry(ze);
        using var sr = new StreamReader(s);
        var raw = sr.ReadToEnd();
        if (string.Equals(contentType, "json", StringComparison.OrdinalIgnoreCase))
        {
            try { payload["body"] = JsonNode.Parse(raw); }
            catch { payload["body"] = raw; /* leave as string if the file isn't JSON */ }
        }
        else
        {
            payload["body"] = raw;
        }
    }

    // Async variant used by the user/role/permission passes which need to
    // resolve a body file by path before the per-pass body lookup dicts
    // (built per-space in RunTailPassesAsync) exist. Takes a global
    // path → entry dict built once at the top of ImportFromEntriesAsync so
    // both the zip and folder sources can resolve siblings the same way.
    private static async Task InlinePayloadBodyAsync(
        JsonObject metaNode, string baseDir,
        IReadOnlyDictionary<string, ImportEntryRef> allByPath, CancellationToken ct)
    {
        if (metaNode["payload"] is not JsonObject payload) return;
        if (payload["body"] is not JsonValue bodyVal) return;
        if (!bodyVal.TryGetValue<string>(out var filename) || string.IsNullOrEmpty(filename)) return;
        var contentType = (payload["content_type"]?.GetValue<string>() ?? "").ToLowerInvariant();
        if (!InlinableContentTypes.Contains(contentType)) return;
        var bodyPath = $"{baseDir}/{filename}".Replace("//", "/");
        if (!allByPath.TryGetValue(bodyPath, out var bodyRef)) return;
        await using var s = OpenEntry(bodyRef);
        using var sr = new StreamReader(s);
        var raw = await sr.ReadToEndAsync(ct);
        if (string.Equals(contentType, "json", StringComparison.OrdinalIgnoreCase))
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

    // Round 3 — when running per-space parallel workers, ZipArchive is NOT
    // thread-safe: concurrent calls to ZipArchiveEntry.Open() on entries
    // from the same archive race on the underlying stream position and
    // corrupt the read ("A local file header is corrupt."). To keep the
    // archive single-threaded while still letting DB work fan out, the main
    // thread pre-reads all tail-pass entry bytes into this AsyncLocal dict
    // before dispatching workers. Helpers below check it through
    // OpenEntry; serial paths leave it null and read from the archive directly.
    private static readonly AsyncLocal<IReadOnlyDictionary<string, byte[]>?> _prefetchedBodies = new();

    // Returns a Stream over the entry's bytes. In serial mode (or folder
    // source mode) this is just the underlying Open(). In zip parallel
    // mode the bytes were pre-read in the main thread and we hand out a
    // fresh non-writable MemoryStream per call — each MemoryStream has its
    // own position so concurrent readers don't race on the underlying
    // buffer. Folder source never populates the prefetch cache (File.OpenRead
    // is thread-safe), so the cache check is a cheap no-op there.
    private static Stream OpenEntry(ImportEntryRef ze)
    {
        var prefetched = _prefetchedBodies.Value;
        if (prefetched is not null && prefetched.TryGetValue(ze.FullName, out var bytes))
            return new MemoryStream(bytes, writable: false);
        return ze.Open();
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(ImportEntryRef ze, CancellationToken ct)
    {
        await using var s = OpenEntry(ze);
        var node = await JsonNode.ParseAsync(s, cancellationToken: ct);
        return node as JsonObject
            ?? throw new InvalidDataException($"{ze.FullName}: expected a JSON object at the root");
    }

    // PostgreSQL's jsonb type cannot store the U+0000 (NUL) character — an
    // insert containing it raises "22P05: unsupported Unicode escape
    // sequence", which under --fast bulk COPY aborts the whole batch.
    // Legacy text data occasionally carries embedded NULs in string fields
    // (e.g. a displayname or body copied from a system that allowed them).
    // We strip them from the in-memory JSON tree before insert; the source
    // file on disk is never modified. Returns the count of string values
    // that were modified, for sidecar reporting.
    private static int StripNullChars(JsonNode? node)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue jv && jv.TryGetValue<string>(out var s)
                        && s.Contains('\0'))
                    {
                        obj[key] = s.Replace("\0", "");
                        count++;
                    }
                    else
                    {
                        count += StripNullChars(child);
                    }
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is JsonValue jv && jv.TryGetValue<string>(out var s)
                        && s.Contains('\0'))
                    {
                        arr[i] = s.Replace("\0", "");
                        count++;
                    }
                    else
                    {
                        count += StripNullChars(child);
                    }
                }
                break;
        }
        return count;
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
