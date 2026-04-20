using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Plugins;
using Dmart.Utils;

namespace Dmart.Services;

public sealed class EntryService(
    EntryRepository entries,
    AttachmentRepository attachments,
    HistoryRepository history,
    PermissionService perms,
    PluginManager plugins,
    SchemaValidator schemas,
    WorkflowEngine workflows,
    ILogger<EntryService> log)
{
    public async Task<Entry?> GetAsync(Locator l, string? actor, CancellationToken ct = default)
    {
        // Load FIRST so the permission gate has the entry's owner/is_active/acl context.
        // Per-entry ACL grants and "own" condition checks both require this — without
        // the entry, those branches of PermissionService can't fire and we'd reject
        // legitimate access. Cost is one extra DB call on the deny path; on the allow
        // path it's free because we'd have loaded the entry anyway.
        var entry = await entries.GetAsync(l.SpaceName, l.Subpath, l.Shortname, l.Type, ct);
        if (entry is null) return null;
        if (!await perms.CanReadAsync(actor, l, PermissionService.FromEntry(entry), ct)) return null;
        return entry;
    }

    public Task<Entry?> GetByUuidAsync(Guid uuid, CancellationToken ct = default)
        => entries.GetByUuidAsync(uuid, ct);

    public Task<Entry?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => entries.GetBySlugAsync(slug, ct);

    public async Task<Result<Entry>> CreateAsync(Entry entry, string? actor, CancellationToken ct = default)
    {
        var locator = new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname);
        // Pass derived attributes for restricted_fields/allowed_fields_values gating.
        // Conditions ("own", "is_active") are not evaluated for create — Python skips
        // them too — so we don't need a ResourceContext here.
        var attrsForGate = EntryToAttributesDict(entry);
        if (!await perms.CanCreateAsync(actor, locator, attrsForGate, ct))
            return Result<Entry>.Fail(InternalErrorCode.NOT_ALLOWED, "no create access", "auth");

        var existing = await entries.GetAsync(entry.SpaceName, entry.Subpath, entry.Shortname, entry.ResourceType, ct);
        if (existing is not null)
            return Result<Entry>.Fail(InternalErrorCode.SHORTNAME_ALREADY_EXIST, "entry exists", "db");

        // dmart's schema validation: if the payload references a schema_shortname,
        // load the schema entry from the same space and validate the body against it.
        var validationError = await ValidatePayloadAsync(entry, ct);
        if (validationError is not null)
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, validationError, "request");

        // Ticket initialization: resolve initial_state from the workflow definition
        // and set is_open = true. Mirrors Python's set_init_state_from_record().
        var ticketState = entry.State;
        var ticketIsOpen = entry.IsOpen;
        if (entry.ResourceType == ResourceType.Ticket && !string.IsNullOrEmpty(entry.WorkflowShortname))
        {
            var initialState = await workflows.GetInitialStateAsync(entry.SpaceName, entry.WorkflowShortname, ct);
            if (initialState is not null)
            {
                ticketState = initialState;
                ticketIsOpen = true;
            }
        }

        var toSave = entry with
        {
            Uuid = string.IsNullOrEmpty(entry.Uuid) ? Guid.NewGuid().ToString() : entry.Uuid,
            OwnerShortname = string.IsNullOrEmpty(entry.OwnerShortname) ? (actor ?? "anonymous") : entry.OwnerShortname,
            State = ticketState,
            IsOpen = ticketIsOpen ?? (entry.ResourceType == ResourceType.Ticket ? true : null),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Fire the BEFORE hook before the DB write. A plugin throwing here should
        // abort the create — we translate the exception into a Result.Fail so the
        // caller can surface a structured error without leaking the stack.
        var beforeEvent = BuildEvent(toSave, ActionType.Create, actor);
        try { await plugins.BeforeActionAsync(beforeEvent, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "before-create plugin hook rejected {Space}/{Subpath}/{Shortname}",
                toSave.SpaceName, toSave.Subpath, toSave.Shortname);
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, "plugin rejected create", "request");
        }

        await entries.UpsertAsync(toSave, ct);

        // If we just wrote a schema entry, invalidate the validator's cache so the
        // new definition is picked up by subsequent payload writes.
        if (toSave.ResourceType == ResourceType.Schema) schemas.ClearCache();

        await history.AppendAsync(toSave.SpaceName, toSave.Subpath, toSave.Shortname, actor, null,
            new() { ["action"] = "create" }, ct);
        await plugins.AfterActionAsync(BuildEvent(toSave, ActionType.Create, actor), ct);
        return Result<Entry>.Ok(toSave);
    }

    private async Task<string?> ValidatePayloadAsync(Entry entry, CancellationToken ct)
    {
        if (entry.Payload is null) return null;
        if (string.IsNullOrEmpty(entry.Payload.SchemaShortname)) return null;
        if (entry.Payload.Body is null) return null;
        if (entry.ResourceType == ResourceType.Schema) return null;  // schemas are themselves JSON Schemas

        var errors = await schemas.ValidateAsync(entry.SpaceName, entry.Payload.SchemaShortname, entry.Payload.Body.Value, ct);
        if (errors is null) return null;
        return "payload failed schema validation: " + string.Join("; ", errors);
    }

    public async Task<Result<Entry>> UpdateAsync(Locator locator, Dictionary<string, object> patch, string? actor, CancellationToken ct = default)
    {
        // Load existing first so the permission check has the resource context for
        // "own"/"is_active" conditions and the patch dict for field-restriction gating.
        var existing = await entries.GetAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, ct);
        if (existing is null)
            return Result<Entry>.Fail(InternalErrorCode.OBJECT_NOT_FOUND, "entry missing", "db");
        if (!await perms.CanUpdateAsync(actor, locator, PermissionService.FromEntry(existing), patch, ct))
            return Result<Entry>.Fail(InternalErrorCode.NOT_ALLOWED, "no update access", "auth");

        var merged = ApplyPatch(existing, patch);

        // Validate the MERGED entry (not the old one) so patches can't bypass schema rules.
        var validationError = await ValidatePayloadAsync(merged, ct);
        if (validationError is not null)
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, validationError, "request");

        var beforeEvent = BuildEvent(merged, ActionType.Update, actor);
        try { await plugins.BeforeActionAsync(beforeEvent, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "before-update plugin hook rejected {Space}/{Subpath}/{Shortname}",
                locator.SpaceName, locator.Subpath, locator.Shortname);
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, "plugin rejected update", "request");
        }

        await entries.UpsertAsync(merged, ct);
        // Invalidate compiled-schema cache if this entry IS a schema. A stale
        // cached schema would validate payloads against the pre-update definition.
        if (merged.ResourceType == ResourceType.Schema) schemas.ClearCache();
        await history.AppendAsync(locator.SpaceName, locator.Subpath, locator.Shortname, actor, null,
            new() { ["action"] = "update", ["patch"] = patch }, ct);
        await plugins.AfterActionAsync(BuildEvent(merged, ActionType.Update, actor), ct);
        return Result<Entry>.Ok(merged);
    }

    public async Task<Result<bool>> DeleteAsync(Locator locator, string? actor, CancellationToken ct = default)
    {
        // Load first so the permission check sees owner/is_active/acl for conditions.
        // If the entry is missing we still report a forbidden-style result via OK(false)
        // — matching the previous behavior of "delete is idempotent if there's nothing
        // to delete and you have permission" but now reachable only when access checks
        // had a resource to gate against.
        var existing = await entries.GetAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, ct);
        var ctx = existing is not null ? PermissionService.FromEntry(existing) : null;
        if (!await perms.CanDeleteAsync(actor, locator, ctx, ct))
            return Result<bool>.Fail(InternalErrorCode.NOT_ALLOWED, "no delete access", "auth");

        // Build a delete Event from whatever we know (prefer the loaded entry so
        // plugin filters on resource_type/schema_shortname see real values).
        var deleteEvent = existing is not null
            ? BuildEvent(existing, ActionType.Delete, actor)
            : new Event
            {
                SpaceName = locator.SpaceName,
                Subpath = locator.Subpath,
                Shortname = locator.Shortname,
                ActionType = ActionType.Delete,
                ResourceType = locator.Type,
                UserShortname = actor ?? "anonymous",
            };

        try { await plugins.BeforeActionAsync(deleteEvent, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "before-delete plugin hook rejected {Space}/{Subpath}/{Shortname}",
                locator.SpaceName, locator.Subpath, locator.Shortname);
            return Result<bool>.Fail(InternalErrorCode.INVALID_DATA, "plugin rejected delete", "request");
        }

        var ok = await entries.DeleteAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, ct);
        if (ok)
        {
            // Schema entry removed → drop any compiled instance from the in-memory cache.
            if (locator.Type == ResourceType.Schema) schemas.ClearCache();
            await history.AppendAsync(locator.SpaceName, locator.Subpath, locator.Shortname, actor, null,
                new() { ["action"] = "delete" }, ct);
            await plugins.AfterActionAsync(deleteEvent, ct);
        }
        return Result<bool>.Ok(ok);
    }

    public async Task<Result<Entry>> MoveAsync(Locator from, Locator to, string? actor, CancellationToken ct = default)
    {
        // Move = update(source) + create(target). Load source so the update check has
        // owner/is_active context. The create check skips conditions per Python.
        var srcEntry = await entries.GetAsync(from.SpaceName, from.Subpath, from.Shortname, from.Type, ct);
        if (srcEntry is null)
            return Result<Entry>.Fail(InternalErrorCode.OBJECT_NOT_FOUND, "source entry missing", "db");
        var srcCtx = PermissionService.FromEntry(srcEntry);
        if (!await perms.CanUpdateAsync(actor, from, srcCtx, null, ct) ||
            !await perms.CanCreateAsync(actor, to, EntryToAttributesDict(srcEntry), ct))
            return Result<Entry>.Fail(InternalErrorCode.NOT_ALLOWED, "no move access", "auth");
        // Python fires a single "move" event keyed on the destination subpath and
        // passes the source shortname as an attribute. Mirror that so hook
        // plugins can see both the old and new names on the same event.
        var moveEvent = new Event
        {
            SpaceName = to.SpaceName,
            Subpath = to.Subpath,
            Shortname = to.Shortname,
            ActionType = ActionType.Move,
            ResourceType = srcEntry.ResourceType,
            SchemaShortname = srcEntry.Payload?.SchemaShortname,
            UserShortname = actor ?? "anonymous",
            Attributes = new() { ["src_shortname"] = from.Shortname, ["src_subpath"] = from.Subpath },
        };
        try { await plugins.BeforeActionAsync(moveEvent, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "before-move plugin hook rejected {FromSpace}/{FromSubpath}/{FromShortname} → {ToSpace}/{ToSubpath}/{ToShortname}",
                from.SpaceName, from.Subpath, from.Shortname, to.SpaceName, to.Subpath, to.Shortname);
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, "plugin rejected move", "request");
        }

        await entries.MoveAsync(from, to, ct);
        await history.AppendAsync(to.SpaceName, to.Subpath, to.Shortname, actor, null,
            new() { ["action"] = "move", ["from"] = $"{from.SpaceName}/{from.Subpath}/{from.Shortname}" }, ct);
        await plugins.AfterActionAsync(moveEvent, ct);
        var moved = await entries.GetAsync(to.SpaceName, to.Subpath, to.Shortname, to.Type, ct);
        return moved is null
            ? Result<Entry>.Fail(InternalErrorCode.OBJECT_NOT_FOUND, "moved entry missing", "db")
            : Result<Entry>.Ok(moved);
    }

    public Task<List<Attachment>> ListAttachmentsAsync(Locator parent, CancellationToken ct = default)
        => attachments.ListForParentAsync(parent.SpaceName, parent.Subpath, parent.Shortname, ct);

    // Builds a plugin-dispatchable Event from an Entry. Mirrors the dict Python
    // assembles at the router level — keeping the attributes key minimal (just
    // state, since that's what the realtime notifier uses) because anything
    // else forces the Event object into a heavier allocation and there's no
    // caller that needs more.
    private static Event BuildEvent(Entry entry, ActionType action, string? actor)
    {
        var attrs = new Dictionary<string, object>(StringComparer.Ordinal);
        if (entry.State is not null) attrs["state"] = entry.State;
        return new Event
        {
            SpaceName = entry.SpaceName,
            Subpath = entry.Subpath,
            Shortname = entry.Shortname,
            ActionType = action,
            ResourceType = entry.ResourceType,
            SchemaShortname = entry.Payload?.SchemaShortname,
            UserShortname = actor ?? "anonymous",
            Attributes = attrs,
        };
    }

    // Synthesizes a flat-ish attributes dict from an Entry's typed fields, used by
    // PermissionService for restricted_fields/allowed_fields_values gating on create.
    // Only the fields that show up on the wire as Record.attributes are included; the
    // ticket-specific fields (state/workflow_shortname/etc.) are inlined when present.
    private static Dictionary<string, object> EntryToAttributesDict(Entry e)
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["is_active"]   = e.IsActive,
            ["tags"]        = e.Tags,
        };
        if (e.Displayname is not null) d["displayname"] = e.Displayname;
        if (e.Description is not null) d["description"] = e.Description;
        if (e.Slug is not null)        d["slug"]        = e.Slug;
        if (e.Payload is not null)     d["payload"]     = e.Payload;
        if (e.State is not null)       d["state"]       = e.State;
        if (e.WorkflowShortname is not null) d["workflow_shortname"] = e.WorkflowShortname;
        return d;
    }

    private static Entry ApplyPatch(Entry existing, Dictionary<string, object> patch)
    {
        string? Str(string key, string? fallback)
            => patch.TryGetValue(key, out var v) && v is not null ? v.ToString() : fallback;

        Translation? PatchTranslation(string key, Translation? fallback)
        {
            if (!patch.TryGetValue(key, out var v) || v is null) return fallback;
            if (v is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Object)
                    return new Translation(
                        En: el.TryGetProperty("en", out var en) ? en.GetString() : fallback?.En,
                        Ar: el.TryGetProperty("ar", out var ar) ? ar.GetString() : fallback?.Ar,
                        Ku: el.TryGetProperty("ku", out var ku) ? ku.GetString() : fallback?.Ku);
                if (el.ValueKind == JsonValueKind.String)
                    return new Translation(En: el.GetString());
                if (el.ValueKind == JsonValueKind.Null)
                    return null;
            }
            if (v is string s)
                return new Translation(En: s);
            return fallback;
        }

        bool? PatchBool(string key, bool? fallback)
        {
            if (!patch.TryGetValue(key, out var v) || v is null) return fallback;
            if (v is bool bv) return bv;
            if (v is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.True) return true;
                if (je.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }

        // Payload body merge: mirrors Python's deep_update(old_body, patch_body)
        // followed by remove_none_dict(). Sending a property as null removes it.
        var payload = existing.Payload;
        if (patch.TryGetValue("payload", out var payloadRaw) && payloadRaw is not null)
        {
            JsonElement? patchBody = null;
            if (payloadRaw is JsonElement pe && pe.ValueKind == JsonValueKind.Object
                && pe.TryGetProperty("body", out var bodyEl))
                patchBody = bodyEl;
            else if (payloadRaw is Dictionary<string, object> pd && pd.TryGetValue("body", out var bodyObj)
                     && bodyObj is JsonElement bje)
                patchBody = bje;

            if (patchBody is not null && payload is not null)
            {
                var merged = DeepMergeAndStripNulls(payload.Body, patchBody.Value);
                payload = payload with { Body = merged };
            }
        }

        return existing with
        {
            Displayname = PatchTranslation("displayname", existing.Displayname),
            Description = PatchTranslation("description", existing.Description),
            Slug = Str("slug", existing.Slug),
            OwnerShortname = Str("owner_shortname", existing.OwnerShortname) ?? existing.OwnerShortname,
            State = Str("state", existing.State),
            IsOpen = PatchBool("is_open", existing.IsOpen),
            WorkflowShortname = Str("workflow_shortname", existing.WorkflowShortname),
            ResolutionReason = Str("resolution_reason", existing.ResolutionReason),
            Tags = patch.TryGetValue("tags", out var tagsRaw) && tagsRaw is IEnumerable<object> tags
                ? tags.Select(t => t?.ToString() ?? "").ToList() : existing.Tags,
            IsActive = patch.TryGetValue("is_active", out var ia) && ia is bool b ? b : existing.IsActive,
            Payload = payload,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Mirrors Python's deep_update(old, patch) + remove_none_dict(result).
    /// - Recursively merges patch into existing body
    /// - Removes any key whose value is null (sending null = delete the key)
    /// </summary>
    private static JsonElement? DeepMergeAndStripNulls(JsonElement? existing, JsonElement patch)
    {
        // If the patch is not an object, just use it directly (same as Python).
        if (patch.ValueKind != JsonValueKind.Object)
            return patch.ValueKind == JsonValueKind.Null ? null : patch;

        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            // Start with all existing properties
            if (existing?.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existing.Value.EnumerateObject())
                {
                    if (patch.TryGetProperty(prop.Name, out var patchVal))
                    {
                        // Key exists in patch — merge or overwrite
                        if (patchVal.ValueKind == JsonValueKind.Null)
                            continue; // null in patch = remove the key
                        if (patchVal.ValueKind == JsonValueKind.Object && prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            // Recursive deep merge
                            var merged = DeepMergeAndStripNulls(prop.Value, patchVal);
                            if (merged is not null)
                            {
                                writer.WritePropertyName(prop.Name);
                                merged.Value.WriteTo(writer);
                            }
                        }
                        else
                        {
                            writer.WritePropertyName(prop.Name);
                            patchVal.WriteTo(writer);
                        }
                    }
                    else
                    {
                        // Key only in existing — keep it (strip if null)
                        if (prop.Value.ValueKind != JsonValueKind.Null)
                        {
                            writer.WritePropertyName(prop.Name);
                            prop.Value.WriteTo(writer);
                        }
                    }
                }
            }

            // Add new keys from patch that don't exist in existing
            foreach (var prop in patch.EnumerateObject())
            {
                if (existing?.ValueKind == JsonValueKind.Object && existing.Value.TryGetProperty(prop.Name, out _))
                    continue; // Already handled above
                if (prop.Value.ValueKind == JsonValueKind.Null)
                    continue; // null = don't add
                writer.WritePropertyName(prop.Name);
                prop.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }
}
