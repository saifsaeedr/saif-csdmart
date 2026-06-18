using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Npgsql;

namespace Dmart.Cli;

// Auto-repair for pre-existing folder payload bodies that don't comply with
// the canonical (management-space) folder_rendering schema, or whose policy
// arrays don't cover the content already inside the folder. Used by
// `dmart fix-folder-rendering [space] [--apply]` so operators can clean a
// legacy deployment BEFORE relying on ENFORCE_FOLDER_CONTENT_POLICY.
//
// Fix rules — deterministic and conservative; CONTENT IS NEVER TOUCHED:
//   1. body fields the canonical schema doesn't define are removed (they were
//      silently ignored before the schema went strict)
//   2. required-but-missing fields get a neutral default (empty array)
//   3. non-empty policy arrays (content_resource_types,
//      content_schema_shortnames, workflow_shortnames) are WIDENED to include
//      what the folder's direct children already use — blessing reality;
//      narrowing is an operator decision, never automatic
//   4. invalid enum values inside content_resource_types are reported but NOT
//      removed — dropping the last entry would flip the folder from
//      "restricted" to "unrestricted" silently
public sealed class FolderRenderingFixer(Db db)
{
    public sealed record FolderFix(
        string Space,
        string Path,
        string FolderSubpath,
        string FolderShortname,
        List<string> RemovedFields,
        List<string> AddedRequired,
        Dictionary<string, List<string>> WidenedArrays,
        List<string> InvalidResourceTypes,
        string? NewBodyJson);

    // Dry-run: plan the fixes for every folder in the space without writing.
    public Task<List<FolderFix>> ScanAsync(string spaceName, CancellationToken ct = default)
        => PlanAsync(spaceName, ct);

    // Apply the planned body rewrites. Returns the number of folders updated.
    public async Task<int> ApplyAsync(string spaceName, CancellationToken ct = default)
    {
        var entries = new EntryRepository(db);
        var applied = 0;
        foreach (var fix in await PlanAsync(spaceName, ct))
        {
            if (fix.NewBodyJson is null) continue;   // report-only finding
            var folder = await entries.GetAsync(
                fix.Space, fix.FolderSubpath, fix.FolderShortname, ResourceType.Folder, ct);
            if (folder?.Payload is null) continue;   // raced away — skip
            using var doc = JsonDocument.Parse(fix.NewBodyJson);
            await entries.UpsertAsync(folder with
            {
                UpdatedAt = DateTime.UtcNow,
                Payload = folder.Payload with { Body = doc.RootElement.Clone() },
            }, ct);
            applied++;
        }
        return applied;
    }

    private async Task<List<FolderFix>> PlanAsync(string spaceName, CancellationToken ct)
    {
        var (allowedProps, required, validResourceTypes) = await LoadCanonicalSchemaAsync(ct);
        var fixes = new List<FolderFix>();

        var folders = new List<(string Subpath, string Shortname, string BodyJson)>();
        await using (var conn = await db.OpenAsync(ct))
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT subpath, shortname, payload->>'body' FROM entries " +
                "WHERE space_name = $1 AND resource_type = 'folder' AND jsonb_typeof(payload->'body') = 'object'",
                conn);
            cmd.Parameters.Add(new() { Value = spaceName });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                folders.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        foreach (var (subpath, shortname, bodyJson) in folders)
        {
            var folderPath = subpath == "/" ? "/" + shortname : subpath + "/" + shortname;
            if (JsonNode.Parse(bodyJson) is not JsonObject node) continue;

            var removed = new List<string>();
            var addedRequired = new List<string>();
            var widened = new Dictionary<string, List<string>>();
            var invalidTypes = new List<string>();

            // (1) strip fields the canonical schema doesn't define
            foreach (var key in node.Select(kv => kv.Key).Where(k => !allowedProps.Contains(k)).ToList())
            {
                node.Remove(key);
                removed.Add(key);
            }

            // (2) required-but-missing → neutral empty array
            foreach (var r in required.Where(r => !node.ContainsKey(r)))
            {
                node[r] = new JsonArray();
                addedRequired.Add(r);
            }

            // (4) report invalid enum values — never removed (see header)
            if (validResourceTypes is not null && node["content_resource_types"] is JsonArray cra)
                foreach (var v in cra)
                    if (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String
                        && !validResourceTypes.Contains(jv.GetValue<string>()))
                        invalidTypes.Add(jv.GetValue<string>());

            // (3) widen non-empty policy arrays to cover existing children
            var (childTypes, childSchemas, childWorkflows) =
                await DirectChildrenAsync(spaceName, folderPath, ct);
            Widen(node, "content_resource_types", childTypes, widened);
            Widen(node, "content_schema_shortnames", childSchemas, widened);
            Widen(node, "workflow_shortnames", childWorkflows, widened);

            var changed = removed.Count > 0 || addedRequired.Count > 0 || widened.Count > 0;
            if (!changed && invalidTypes.Count == 0) continue;
            fixes.Add(new FolderFix(
                spaceName, folderPath, subpath, shortname,
                removed, addedRequired, widened, invalidTypes,
                changed ? node.ToJsonString() : null));
        }
        return fixes;
    }

    // Reads the canonical schema from the management space: the set of legal
    // body fields, the required list, and (when the schema constrains them)
    // the legal content_resource_types values.
    private async Task<(HashSet<string> AllowedProps, List<string> Required, HashSet<string>? ValidResourceTypes)>
        LoadCanonicalSchemaAsync(CancellationToken ct)
    {
        var entries = new EntryRepository(db);
        Entry? schemaEntry = null;
        foreach (var sub in new[] { "/schema", "/schemas" })
        {
            schemaEntry = await entries.GetAsync("management", sub, "folder_rendering", ResourceType.Schema, ct);
            if (schemaEntry is not null) break;
        }
        if (schemaEntry?.Payload?.Body is not JsonElement schemaBody
            || schemaBody.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                "canonical folder_rendering schema not found in the management space — run `dmart seed` first");

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? validTypes = null;
        if (schemaBody.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject()) allowed.Add(p.Name);
            if (props.TryGetProperty("content_resource_types", out var crt)
                && crt.ValueKind == JsonValueKind.Object
                && crt.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("enum", out var en)
                && en.ValueKind == JsonValueKind.Array)
            {
                validTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var v in en.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) validTypes.Add(v.GetString()!);
            }
        }
        var required = new List<string>();
        if (schemaBody.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString()!);
        return (allowed, required, validTypes);
    }

    private async Task<(HashSet<string> Types, HashSet<string> Schemas, HashSet<string> Workflows)>
        DirectChildrenAsync(string spaceName, string folderPath, CancellationToken ct)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        var schemas = new HashSet<string>(StringComparer.Ordinal);
        var workflows = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT DISTINCT e.resource_type::text, e.payload->>'schema_shortname', " +
            "CASE WHEN e.resource_type = 'ticket' THEN e.workflow_shortname END " +
            "FROM entries e WHERE e.space_name = $1 AND e.subpath = $2", conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = folderPath });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            types.Add(reader.GetString(0));
            if (!reader.IsDBNull(1)) schemas.Add(reader.GetString(1));
            if (!reader.IsDBNull(2)) workflows.Add(reader.GetString(2));
        }
        return (types, schemas, workflows);
    }

    // Append observed-but-missing values to a NON-EMPTY policy array. An empty
    // or absent array means "unrestricted" — widening it would NARROW the
    // policy, so it is left alone.
    private static void Widen(
        JsonObject node, string key, HashSet<string> observed,
        Dictionary<string, List<string>> widened)
    {
        if (observed.Count == 0) return;
        if (node[key] is not JsonArray arr || arr.Count == 0) return;
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in arr)
            if (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String)
                existing.Add(jv.GetValue<string>());
        var missing = observed.Where(o => !existing.Contains(o))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (missing.Count == 0) return;
        // The (JsonNode?) cast forces the non-generic Add(JsonNode?) overload —
        // the generic JsonArray.Add<T> is RequiresUnreferencedCode/-DynamicCode
        // and fails the AOT zero-warning build even for T=JsonValue.
        foreach (var m in missing) arr.Add((JsonNode?)JsonValue.Create(m));
        widened[key] = missing;
    }
}
