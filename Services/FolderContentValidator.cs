using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Enforces a folder's content-policy fields on the resources created/updated
// (or moved) directly inside it. Sibling of UniquenessValidator: same
// parent-folder lookup, reads three arrays from the folder's payload.body —
//   - content_resource_types     : the resource_type must be one of these
//   - content_schema_shortnames  : an entry that DECLARES a payload.schema_shortname
//                                  must use one of these
//   - workflow_shortnames        : a Ticket that DECLARES a workflow_shortname must
//                                  use one of these
//
// Rules (see the design spec):
//   * empty or absent list  -> no restriction on that dimension
//   * absent incoming value -> that dimension's gate is skipped (so schema-less
//     entries and identity resources are never rejected for "having no schema",
//     and a non-ticket / workflow-less ticket ignores workflow_shortnames).
//     NOTE this makes the schema dimension advisory on its own — an entry can
//     omit schema_shortname entirely and pass; combine with
//     content_resource_types when the folder must be airtight.
//   * no parent folder / unreadable folder -> allow (fail open), matching
//     UniquenessValidator
//
// ENFORCEMENT IS OPT-IN: these arrays predate server-side enforcement (they
// were CXB rendering hints), so deployed folders may already carry them. With
// EnforceFolderContentPolicy=false (the default) the validator runs in
// dry-run mode — violations are warn-logged but allowed — so operators can
// review/bless existing data before setting ENFORCE_FOLDER_CONTENT_POLICY=true.
//
// Attachments (Comment/Media/...) attach UNDER a content entry, so their parent
// resolves to a non-folder and this validator no-ops — identical to how
// UniquenessValidator already behaves. The gate only fires for attachments
// attached directly to a Folder, which is the intended edge.
public sealed class FolderContentValidator(
    EntryRepository entries,
    ILogger<FolderContentValidator> log,
    IOptions<DmartSettings> settings)
{
    // Entry path (EntryService create/update/move). On update, pass the
    // post-merge entry; on move, the entry rebased onto the DESTINATION locator.
    public async Task<Result<bool>> ValidateAsync(Entry entry, CancellationToken ct = default)
    {
        var body = await LoadFolderBodyAsync(entry.SpaceName, entry.Subpath, ct);
        if (body is null) return Result<bool>.Ok(true);
        return Gate(
            Check(entry.ResourceType, entry.Payload?.SchemaShortname, entry.WorkflowShortname, body.Value),
            entry.SpaceName, entry.Subpath);
    }

    // Raw-attrs path (RequestHandler non-entry create handlers). `shortname` is
    // accepted for call-site symmetry with UniquenessValidator.ValidateRawAsync.
    public async Task<Result<bool>> ValidateRawAsync(
        string spaceName, string subpath, string shortname, ResourceType resourceType,
        Dictionary<string, object>? rawAttrs, CancellationToken ct = default)
    {
        var body = await LoadFolderBodyAsync(spaceName, subpath, ct);
        if (body is null) return Result<bool>.Ok(true);
        var (schema, workflow) = ExtractSchemaAndWorkflow(rawAttrs, spaceName, subpath);
        return Gate(Check(resourceType, schema, workflow, body.Value), spaceName, subpath);
    }

    // Dry-run vs enforce: a violation only rejects when the operator opted in;
    // otherwise it is warn-logged (this is the signal to watch before flipping
    // ENFORCE_FOLDER_CONTENT_POLICY=true) and the write proceeds.
    private Result<bool> Gate(Result<bool> check, string spaceName, string subpath)
    {
        if (check.IsOk || settings.Value.EnforceFolderContentPolicy) return check;
        log.LogWarning(
            "folder-content policy violation (NOT enforced) at {Space}{Subpath}: {Reason} — "
            + "set ENFORCE_FOLDER_CONTENT_POLICY=true to reject such writes",
            spaceName, subpath, check.ErrorMessage);
        return Result<bool>.Ok(true);
    }

    private static Result<bool> Check(
        ResourceType resourceType, string? schemaShortname, string? workflowShortname, JsonElement body)
    {
        if (TryGetNonEmptyStringArray(body, "content_resource_types", out var allowedTypes))
        {
            var rt = ResourceTypeJsonConverter.ToWire(resourceType);
            if (!ArrayContains(allowedTypes, rt))
                return Result<bool>.Fail(InternalErrorCode.INVALID_DATA,
                    $"resource type '{rt}' is not permitted in this folder", ErrorTypes.Request);
        }

        if (!string.IsNullOrEmpty(schemaShortname)
            && TryGetNonEmptyStringArray(body, "content_schema_shortnames", out var allowedSchemas)
            && !ArrayContains(allowedSchemas, schemaShortname))
            return Result<bool>.Fail(InternalErrorCode.INVALID_DATA,
                $"schema '{schemaShortname}' is not permitted in this folder", ErrorTypes.Request);

        if (resourceType == ResourceType.Ticket
            && !string.IsNullOrEmpty(workflowShortname)
            && TryGetNonEmptyStringArray(body, "workflow_shortnames", out var allowedWorkflows)
            && !ArrayContains(allowedWorkflows, workflowShortname))
            return Result<bool>.Fail(InternalErrorCode.INVALID_DATA,
                $"workflow '{workflowShortname}' is not permitted in this folder", ErrorTypes.Request);

        return Result<bool>.Ok(true);
    }

    // Loads the folder that owns `subpath` and returns its payload.body if it's a
    // JSON object; null when there is no parent folder, the folder is missing, the
    // load throws, or the body isn't an object — all "allow" cases.
    private async Task<JsonElement?> LoadFolderBodyAsync(string spaceName, string subpath, CancellationToken ct)
    {
        var (parentSubpath, folderShortname) = SplitSubpath(subpath);
        if (folderShortname.Length == 0) return null;

        Entry? folder;
        try
        {
            folder = await entries.GetAsync(spaceName, parentSubpath, folderShortname, ResourceType.Folder, ct);
        }
        catch (Exception ex)
        {
            // Warning, not debug: this is a policy gate failing OPEN — operators
            // should be able to see when enforcement was waived by a DB hiccup.
            log.LogWarning(ex, "folder-content: parent folder load failed for {Space}/{Subpath} — policy not applied", spaceName, subpath);
            return null;
        }
        // Body is a standalone JsonElement (heap-owned document produced by
        // JsonSerializer.Deserialize for the JsonElement? property), so it outlives
        // this call and Check can read it without a Clone().
        if (folder?.Payload?.Body is JsonElement body && body.ValueKind == JsonValueKind.Object)
            return body;
        return null;
    }

    // Pulls schema_shortname (from payload.schema_shortname, or a flat
    // schema_shortname) and workflow_shortname out of the raw request attributes.
    // Serializes the dict to a JsonElement first so JsonElement/dict/JsonNode
    // inputs all normalize uniformly (same trick UniquenessValidator uses). A
    // serialization failure only weakens the schema/workflow gates — the
    // resource-type gate still applies since it uses the passed-in resourceType.
    private (string? schema, string? workflow) ExtractSchemaAndWorkflow(
        Dictionary<string, object>? rawAttrs, string spaceName, string subpath)
    {
        if (rawAttrs is null) return (null, null);
        JsonElement root;
        try { root = JsonSerializer.SerializeToElement(rawAttrs, DmartJsonContext.Default.DictionaryStringObject); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "folder-content: failed to materialize rawAttrs for {Space}{Subpath}", spaceName, subpath);
            return (null, null);
        }
        if (root.ValueKind != JsonValueKind.Object) return (null, null);

        string? schema = null;
        if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("schema_shortname", out var ss) && ss.ValueKind == JsonValueKind.String)
            schema = ss.GetString();
        else if (root.TryGetProperty("schema_shortname", out var flat) && flat.ValueKind == JsonValueKind.String)
            schema = flat.GetString();

        string? workflow = null;
        if (root.TryGetProperty("workflow_shortname", out var wf) && wf.ValueKind == JsonValueKind.String)
            workflow = wf.GetString();

        return (schema, workflow);
    }

    private static bool TryGetNonEmptyStringArray(JsonElement body, string key, out JsonElement arr)
    {
        arr = default;
        if (!body.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array || el.GetArrayLength() == 0)
            return false;
        arr = el;
        return true;
    }

    private static bool ArrayContains(JsonElement arr, string value)
    {
        foreach (var el in arr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && string.Equals(el.GetString(), value, StringComparison.Ordinal))
                return true;
        return false;
    }

    // Locator-normalized split (copied from UniquenessValidator): the DB stores
    // subpath with a leading slash, so the parent must too.
    //   "/products/electronics" -> ("/products", "electronics")
    //   "/cat"                  -> ("/",         "cat")
    //   "/" or ""               -> ("/",         "")  — no folder, caller allows
    private static (string parentSubpath, string folderShortname) SplitSubpath(string subpath)
    {
        var s = (subpath ?? "").Trim();
        while (s.StartsWith('/')) s = s[1..];
        if (s.Length == 0) return ("/", "");
        var ix = s.LastIndexOf('/');
        if (ix < 0) return ("/", s);
        return ("/" + s[..ix], s[(ix + 1)..]);
    }
}
