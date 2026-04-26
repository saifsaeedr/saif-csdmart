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

    // IDOR guard: the entry is fetched by UUID/slug (no space/subpath in the URL),
    // so we must look up its Locator and run the same CanReadAsync gate GetAsync
    // uses. Without this, any authenticated caller who knows a UUID could read
    // entries they have no ACL path to.
    public async Task<Entry?> GetByUuidAsync(Guid uuid, string? actor, CancellationToken ct = default)
    {
        var entry = await entries.GetByUuidAsync(uuid, ct);
        if (entry is null) return null;
        var l = new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname);
        if (!await perms.CanReadAsync(actor, l, PermissionService.FromEntry(entry), ct)) return null;
        return entry;
    }

    public async Task<Entry?> GetBySlugAsync(string slug, string? actor, CancellationToken ct = default)
    {
        var entry = await entries.GetBySlugAsync(slug, ct);
        if (entry is null) return null;
        var l = new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname);
        if (!await perms.CanReadAsync(actor, l, PermissionService.FromEntry(entry), ct)) return null;
        return entry;
    }

    public async Task<Result<Entry>> CreateAsync(Entry entry, string? actor, CancellationToken ct = default)
        => await CreateAsync(entry, actor, rawAttrs: null, ct);

    // Overload that accepts the client's raw record.attributes dict for the
    // restricted_fields / allowed_fields_values gate. Python parity: the
    // create check is passed `record.attributes` as-submitted — synthesizing
    // defaults like is_active=true or tags=[] on the gate side would deny
    // requests Python allows when a permission has those fields restricted.
    public async Task<Result<Entry>> CreateAsync(
        Entry entry, string? actor, Dictionary<string, object>? rawAttrs, CancellationToken ct = default)
    {
        var locator = new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname);
        // Python passes the raw client attributes (no synthesized defaults). When
        // a caller doesn't have them (CSV import, plugin-internal writes), fall
        // back to the derived dict — that's strictly a superset so any deny
        // there is at least as safe as Python would be for the same write.
        var attrsForGate = rawAttrs ?? EntryToAttributesDict(entry);
        if (!await perms.CanCreateAsync(actor, locator, attrsForGate, ct))
            return Result<Entry>.Fail(InternalErrorCode.NOT_ALLOWED, "no create access", ErrorTypes.Auth);

        var existing = await entries.GetAsync(entry.SpaceName, entry.Subpath, entry.Shortname, entry.ResourceType, ct);
        if (existing is not null)
            return Result<Entry>.Fail(InternalErrorCode.SHORTNAME_ALREADY_EXIST, "entry exists", ErrorTypes.Db);

        // dmart's schema validation: if the payload references a schema_shortname,
        // load the schema entry from the same space and validate the body against it.
        var validationError = await ValidatePayloadAsync(entry, ct);
        if (validationError is not null)
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, validationError, ErrorTypes.Request);

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
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, "plugin rejected create", ErrorTypes.Request);
        }

        await entries.UpsertAsync(toSave, ct);

        // If we just wrote a schema entry, invalidate the validator's cache so the
        // new definition is picked up by subsequent payload writes.
        if (toSave.ResourceType == ResourceType.Schema) schemas.ClearCache();

        // Python doesn't write a history row on create (adapter.py::save does
        // not call store_entry_diff), so skip the append here. Mirrors the
        // /managed/query?type=history response shape — every row's diff is
        // `{field: {old, new}}`, never a synthetic `{action:"create"}` envelope.
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

    public async Task<Result<Entry>> UpdateAsync(
        Locator locator,
        Dictionary<string, object> patch,
        string? actor,
        CancellationToken ct = default,
        bool allowRestrictedFields = false)
    {
        // Load existing first so the permission check has the resource context for
        // "own"/"is_active" conditions and the patch dict for field-restriction gating.
        var existing = await entries.GetAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, ct);
        if (existing is null)
            return Result<Entry>.Fail(InternalErrorCode.OBJECT_NOT_FOUND, "entry missing", ErrorTypes.Db);
        if (!await perms.CanUpdateAsync(actor, locator, PermissionService.FromEntry(existing), patch, ct))
            return Result<Entry>.Fail(InternalErrorCode.NOT_ALLOWED, "no update access", ErrorTypes.Auth);

        var merged = ApplyPatch(existing, patch, allowRestrictedFields);

        // Validate the MERGED entry (not the old one) so patches can't bypass schema rules.
        var validationError = await ValidatePayloadAsync(merged, ct);
        if (validationError is not null)
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, validationError, ErrorTypes.Request);

        var beforeEvent = BuildEvent(merged, ActionType.Update, actor);
        try { await plugins.BeforeActionAsync(beforeEvent, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "before-update plugin hook rejected {Space}/{Subpath}/{Shortname}",
                locator.SpaceName, locator.Subpath, locator.Shortname);
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, "plugin rejected update", ErrorTypes.Request);
        }

        await entries.UpsertAsync(merged, ct);
        // Invalidate compiled-schema cache if this entry IS a schema. A stale
        // cached schema would validate payloads against the pre-update definition.
        if (merged.ResourceType == ResourceType.Schema) schemas.ClearCache();

        // Python parity: history.diff is stored as {field_path: {old, new}}.
        // The same diff powers after_action's attributes.history_diff so both
        // the persisted history row and the plugin event share one source of
        // truth. Previously we wrote `{action:"update", patch:{...}}` into the
        // diff column — broke the /managed/query?type=history response shape
        // which clients expect to be `{old, new}`-only per key.
        var historyDiff = ComputeHistoryDiff(existing, merged);
        await history.AppendAsync(locator.SpaceName, locator.Subpath, locator.Shortname, actor, null,
            historyDiff.Count > 0 ? historyDiff : null, ct);

        var afterEvent = BuildEvent(merged, ActionType.Update, actor);
        if (historyDiff.Count > 0) afterEvent.Attributes["history_diff"] = historyDiff;
        await plugins.AfterActionAsync(afterEvent, ct);
        return Result<Entry>.Ok(merged);
    }

    // Mirror of dmart's adapter.py::store_entry_diff: flatten both Entries,
    // produce a {key: {"old": …, "new": …}} dict for every field whose value
    // changed. Empty dict when nothing relevant differs. Used by the
    // AfterAction event so plugins can introspect exactly what changed.
    private static Dictionary<string, object> ComputeHistoryDiff(Entry oldE, Entry newE)
    {
        var oldFlat = FlattenEntry(oldE);
        var newFlat = FlattenEntry(newE);
        var keys = new HashSet<string>(oldFlat.Keys, StringComparer.Ordinal);
        keys.UnionWith(newFlat.Keys);

        var diff = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var k in keys)
        {
            oldFlat.TryGetValue(k, out var o);
            newFlat.TryGetValue(k, out var n);
            if (ValuesEqual(o, n)) continue;
            diff[k] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["old"] = o,
                ["new"] = n,
            };
        }
        return diff;
    }

    private static Dictionary<string, object?> FlattenEntry(Entry e)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = e.IsActive,
            ["owner_shortname"] = e.OwnerShortname,
            ["owner_group_shortname"] = e.OwnerGroupShortname,
            ["slug"] = e.Slug,
            ["tags"] = e.Tags ?? new(),
            ["state"] = e.State,
            ["is_open"] = e.IsOpen,
            ["workflow_shortname"] = e.WorkflowShortname,
            ["resolution_reason"] = e.ResolutionReason,
        };
        if (e.Displayname is not null)
        {
            d["displayname.en"] = e.Displayname.En;
            d["displayname.ar"] = e.Displayname.Ar;
            d["displayname.ku"] = e.Displayname.Ku;
        }
        if (e.Description is not null)
        {
            d["description.en"] = e.Description.En;
            d["description.ar"] = e.Description.Ar;
            d["description.ku"] = e.Description.Ku;
        }
        if (e.Payload?.Body is JsonElement body)
            FlattenJson(body, "payload.body", d);
        return d;
    }

    private static void FlattenJson(JsonElement el, string prefix, Dictionary<string, object?> outDict)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    FlattenJson(prop.Value, $"{prefix}.{prop.Name}", outDict);
                break;
            case JsonValueKind.Array:
                // Python flatten_dict leaves arrays as whole list values at
                // their key and then compares them with `!=` (semantic
                // equality). Store the cloned JsonElement so ValuesEqual can
                // walk it structurally — using GetRawText() here compared two
                // semantically-equal arrays as unequal whenever JSONB's
                // canonical form differed from the client's re-serialized form
                // (whitespace, key order inside inner objects, numeric
                // representation). That made unchanged arrays appear in the
                // history_diff on every update.
                outDict[prefix] = el.Clone();
                break;
            case JsonValueKind.String:
                outDict[prefix] = el.GetString();
                break;
            case JsonValueKind.Number:
                outDict[prefix] = el.TryGetInt64(out var i) ? (object)i : el.GetDouble();
                break;
            case JsonValueKind.True:  outDict[prefix] = true; break;
            case JsonValueKind.False: outDict[prefix] = false; break;
            case JsonValueKind.Null:  outDict[prefix] = null; break;
        }
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is JsonElement ja && b is JsonElement jb) return JsonElementEquals(ja, jb);
        if (a is System.Collections.IEnumerable ae && a is not string
            && b is System.Collections.IEnumerable be && b is not string)
        {
            var al = new List<object?>();
            foreach (var x in ae) al.Add(x);
            var bl = new List<object?>();
            foreach (var x in be) bl.Add(x);
            if (al.Count != bl.Count) return false;
            for (var i = 0; i < al.Count; i++)
                if (!ValuesEqual(al[i], bl[i])) return false;
            return true;
        }
        return Equals(a, b);
    }

    // Structural JSON equality — same semantics as Python's `==` on the
    // deserialized (dict / list / primitive) tree. Key order is ignored for
    // objects, so JSONB's canonicalization doesn't show up as a diff.
    private static bool JsonElementEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            // Allow int↔double when numerically equal — JSONB normalizes to
            // a single numeric representation, clients may send ints as either.
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) { }
            else return false;
        }
        switch (a.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return a.GetDouble() == b.GetDouble();
            case JsonValueKind.Array:
            {
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                using var aEnum = a.EnumerateArray().GetEnumerator();
                using var bEnum = b.EnumerateArray().GetEnumerator();
                while (aEnum.MoveNext() && bEnum.MoveNext())
                    if (!JsonElementEquals(aEnum.Current, bEnum.Current)) return false;
                return true;
            }
            case JsonValueKind.Object:
            {
                var aKeys = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in a.EnumerateObject()) aKeys[p.Name] = p.Value;
                var bKeys = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in b.EnumerateObject()) bKeys[p.Name] = p.Value;
                if (aKeys.Count != bKeys.Count) return false;
                foreach (var (k, av) in aKeys)
                {
                    if (!bKeys.TryGetValue(k, out var bv)) return false;
                    if (!JsonElementEquals(av, bv)) return false;
                }
                return true;
            }
            default:
                return false;
        }
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
            return Result<bool>.Fail(InternalErrorCode.NOT_ALLOWED, "no delete access", ErrorTypes.Auth);

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
            return Result<bool>.Fail(InternalErrorCode.INVALID_DATA, "plugin rejected delete", ErrorTypes.Request);
        }

        var ok = await entries.DeleteAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, ct);
        if (ok)
        {
            // Schema entry removed → drop any compiled instance from the in-memory cache.
            if (locator.Type == ResourceType.Schema) schemas.ClearCache();
            // Python doesn't write a history row on delete — the entry (and
            // its per-entry history) is gone by this point anyway. Keeping
            // parity here avoids `/managed/query?type=history` returning
            // rows whose diff isn't the standard `{field: {old, new}}` shape.
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
            return Result<Entry>.Fail(InternalErrorCode.OBJECT_NOT_FOUND, "source entry missing", ErrorTypes.Db);
        var srcCtx = PermissionService.FromEntry(srcEntry);
        if (!await perms.CanUpdateAsync(actor, from, srcCtx, null, ct) ||
            !await perms.CanCreateAsync(actor, to, EntryToAttributesDict(srcEntry), ct))
            return Result<Entry>.Fail(InternalErrorCode.NOT_ALLOWED, "no move access", ErrorTypes.Auth);
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
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, "plugin rejected move", ErrorTypes.Request);
        }

        await entries.MoveAsync(from, to, ct);
        // Python parity: no history row on move. Destination gets a fresh
        // entry; the source's history stays with the old row (which is now
        // gone). Keeping the /managed/query?type=history response shape
        // uniformly `{field: {old, new}}` means no action-envelope rows.
        await plugins.AfterActionAsync(moveEvent, ct);
        var moved = await entries.GetAsync(to.SpaceName, to.Subpath, to.Shortname, to.Type, ct);
        return moved is null
            ? Result<Entry>.Fail(InternalErrorCode.OBJECT_NOT_FOUND, "moved entry missing", ErrorTypes.Db)
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

    private static Entry ApplyPatch(Entry existing, Dictionary<string, object> patch, bool allowRestrictedFields)
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
                var merged = JsonMerge.DeepMergeAndStripNulls(payload.Body, patchBody.Value);
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
            // Python parity: `acl` lives in Meta.restricted_fields and is only
            // writable through the dedicated update_acl path. Regular update
            // ignores it; DispatchUpdateAclAsync opts in via
            // allowRestrictedFields=true.
            Acl = allowRestrictedFields && patch.ContainsKey("acl")
                ? ParsePatchAcl(patch)
                : existing.Acl,
            Payload = payload,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    // Mirrors RequestHandler.ParseAcl; kept local so EntryService doesn't
    // reach across into the API layer.
    private static List<AclEntry>? ParsePatchAcl(Dictionary<string, object> patch)
    {
        if (!patch.TryGetValue("acl", out var raw) || raw is null) return null;
        if (raw is not JsonElement arr || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<AclEntry>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var user = item.TryGetProperty("user_shortname", out var us) && us.ValueKind == JsonValueKind.String
                ? us.GetString() : null;
            if (string.IsNullOrEmpty(user)) continue;
            List<string>? allowed = null;
            if (item.TryGetProperty("allowed_actions", out var aa) && aa.ValueKind == JsonValueKind.Array)
            {
                allowed = new List<string>();
                foreach (var a in aa.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String && a.GetString() is { } s) allowed.Add(s);
            }
            List<string>? denied = null;
            if (item.TryGetProperty("denied", out var dd) && dd.ValueKind == JsonValueKind.Array)
            {
                denied = new List<string>();
                foreach (var d in dd.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.String && d.GetString() is { } s) denied.Add(s);
            }
            list.Add(new AclEntry { UserShortname = user, AllowedActions = allowed, Denied = denied });
        }
        return list;
    }

}
