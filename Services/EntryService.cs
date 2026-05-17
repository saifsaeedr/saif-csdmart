using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Npgsql;

namespace Dmart.Services;

public sealed class EntryService(
    EntryRepository entries,
    AttachmentRepository attachments,
    HistoryRepository history,
    PermissionService perms,
    PluginManager plugins,
    SchemaValidator schemas,
    WorkflowEngine workflows,
    ILogger<EntryService> log,
    UniquenessValidator? uniqueness = null)
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
        => await CreateAsync(entry, actor, rawAttrs: null, isBulkImport: false, ct);

    // Overload that accepts the client's raw record.attributes dict for the
    // restricted_fields / allowed_fields_values gate. Python parity: the
    // create check is passed `record.attributes` as-submitted — synthesizing
    // defaults like is_active=true or tags=[] on the gate side would deny
    // requests Python allows when a permission has those fields restricted.
    // `isBulkImport=true` tags emitted Events so logging-only hooks (audit)
    // skip per-row noise during /resources_from_csv etc.; other hooks still fire.
    public async Task<Result<Entry>> CreateAsync(
        Entry entry, string? actor, Dictionary<string, object>? rawAttrs,
        bool isBulkImport = false, CancellationToken ct = default)
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

        // Referential integrity: every relationship's related_to locator must
        // resolve to a live entry before we persist this row. Without this gate
        // a caller could create dangling references that read paths would later
        // surface as 404s. Mirrors Python adapter.py::_validate_referential_integrity
        // but for entries-table types (Python only gates user/role/permission/group;
        // the parity here is the *pattern*, not the scope — content entries also
        // benefit from refusing to land orphans).
        var relError = await ValidateRelationshipsAsync(entry.Relationships, ct);
        if (relError is not null)
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, relError, ErrorTypes.Request);

        // Folder-level compound-key uniqueness (Python parity:
        // adapter.py::validate_uniqueness). Runs before plugins so a
        // before-create hook can rely on the constraint having been checked.
        if (uniqueness is not null)
        {
            var uniqRes = await uniqueness.ValidateAsync(entry, ActionType.Create, existing: null, ct);
            if (!uniqRes.IsOk)
                return Result<Entry>.Fail(uniqRes.ErrorCode, uniqRes.ErrorMessage!, uniqRes.ErrorType!);
        }

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
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        };

        // Fire the BEFORE hook before the DB write. A plugin throwing here should
        // abort the create — we translate the exception into a Result.Fail so the
        // caller can surface a structured error without leaking the stack.
        var beforeEvent = BuildEvent(toSave, ActionType.Create, actor, isBulkImport);
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
        await plugins.AfterActionAsync(BuildEvent(toSave, ActionType.Create, actor, isBulkImport), ct);
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

    // Walks the relationships list and verifies each related_to locator
    // resolves to a row in `entries`. Returns null on success, a single-line
    // error message (containing the literal "relationship target not found"
    // anchor the integration tests pin on) on the first miss in source order
    // so callers can surface a structured INVALID_DATA without leaking full
    // locator dumps.
    //
    // Batched: one EntryRepository.ExistMaskAsync round trip regardless of
    // ref count. The earlier per-rel GetAsync loop was O(N) round trips on
    // every create and on every update that added refs.
    //
    // Scope: user/role/permission/space live in their own tables, so we
    // can't probe them via EntryRepository; skipping those keeps the gate
    // honest (no false positives on cross-table refs) while still catching
    // the common case — relationships from content entries to other content
    // entries, tickets, folders, schemas, posts, etc.
    //
    // TOCTOU note: a target deleted between validate and the subsequent
    // upsert would land a dangling ref. We accept that gap rather than wrap
    // the upsert in a transaction with a row lock — the converse (reverse-
    // RI on delete) catches the practical case, and concurrent deletes
    // during a write are vanishingly rare.
    private async Task<string?> ValidateRelationshipsAsync(
        List<Dictionary<string, object>>? relationships, CancellationToken ct)
    {
        if (relationships is null || relationships.Count == 0) return null;
        var ordered = new List<Locator>();
        foreach (var rel in relationships)
        {
            if (rel is null) continue;
            if (!rel.TryGetValue("related_to", out var rawTarget) || rawTarget is null) continue;
            var target = ParseLocator(rawTarget);
            if (target is null) continue;  // malformed shape — let schema validation surface it, not RI.
            // Out-of-table targets pass through. ExistMaskAsync only probes
            // `entries`, so probing user/role/permission/space here would
            // always 404 even when the target exists elsewhere.
            if (target.Type is ResourceType.User or ResourceType.Role
                or ResourceType.Permission or ResourceType.Space) continue;
            ordered.Add(target);
        }
        if (ordered.Count == 0) return null;

        var probes = new List<(string SpaceName, string Subpath, string Shortname)>(ordered.Count);
        foreach (var l in ordered) probes.Add((l.SpaceName, l.Subpath, l.Shortname));
        var hits = await entries.ExistMaskAsync(probes, ct);

        foreach (var target in ordered)
        {
            if (!hits.Contains((target.SpaceName, target.Subpath, target.Shortname)))
                return $"relationship target not found: {target.SpaceName}{target.Subpath}/{target.Shortname} ({JsonbHelpers.EnumMember(target.Type)})";
        }
        return null;
    }

    // Parses one related_to dict into a Locator. Tolerates both shapes the
    // dict can land in: nested Dictionary<string, object> (in-process plugin
    // callers) and JsonElement of Object kind (HTTP callers — source-gen
    // deserializes nested object values as JsonElement). Returns null when
    // any required field (space_name / subpath / shortname / type) is
    // missing or unparseable — the caller treats that as "skip" rather
    // than "fail" since the schema validator owns shape errors.
    private static Locator? ParseLocator(object raw)
    {
        string? type = null, spaceName = null, subpath = null, shortname = null;
        if (raw is Dictionary<string, object> dict)
        {
            type = TryReadString(dict, "type");
            spaceName = TryReadString(dict, "space_name");
            subpath = TryReadString(dict, "subpath");
            shortname = TryReadString(dict, "shortname");
        }
        else if (raw is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
            type = TryReadJsonString(el, "type");
            spaceName = TryReadJsonString(el, "space_name");
            subpath = TryReadJsonString(el, "subpath");
            shortname = TryReadJsonString(el, "shortname");
        }
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(spaceName)
            || string.IsNullOrEmpty(shortname)) return null;
        ResourceType rt;
        try { rt = JsonbHelpers.ParseEnumMember<ResourceType>(type); }
        catch { return null; }  // unknown type → shape error, skip.
        return new Locator(rt, spaceName, subpath ?? "/", shortname);
    }

    private static string? TryReadString(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return s;
        if (v is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString();
        return v.ToString();
    }

    private static string? TryReadJsonString(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Patch dicts are typed `Dictionary<string, object>` but System.Text.Json's
    // source generator deserializes JSON `null` into a `JsonElement` of
    // `ValueKind.Null` (a struct value), not CLR `null`. So a check like
    // `raw is null` only fires for in-process callers passing literal null
    // and misses the HTTP wire shape entirely. This helper collapses both
    // shapes into one predicate so patch handlers can rely on "key present
    // + value indicates clear" being a single check.
    internal static bool IsPatchNull(object? raw)
        => raw is null || (raw is JsonElement el && el.ValueKind == JsonValueKind.Null);

    // Returns the subset of `next` whose related_to locator did NOT appear in
    // `prev`. Compared by the four locator fields (type / space_name /
    // subpath / shortname) — uuid/domain/attributes are ignored because they
    // don't move the row. Both lists may contain JsonElement-boxed or
    // Dictionary-typed values; ParseLocator normalizes the comparison key
    // either way. Used by UpdateAsync to validate only freshly added refs.
    //
    // Consequence: an edit that keeps the same locator but mutates the
    // relationship's `attributes` payload is NOT considered "new" → the RI
    // gate does not re-run. That's by design — the locator is what RI
    // protects; attributes are arbitrary metadata. Worth knowing if you're
    // chasing a "why didn't validation catch X" question.
    private static List<Dictionary<string, object>>? DiffNewRelationships(
        List<Dictionary<string, object>>? prev, List<Dictionary<string, object>>? next)
    {
        if (next is null || next.Count == 0) return null;
        if (prev is null || prev.Count == 0) return next;
        var prevKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in prev)
        {
            if (rel is null) continue;
            if (rel.TryGetValue("related_to", out var t) && t is not null)
            {
                var loc = ParseLocator(t);
                if (loc is not null) prevKeys.Add(LocatorKey(loc));
            }
        }
        var added = new List<Dictionary<string, object>>();
        foreach (var rel in next)
        {
            if (rel is null) continue;
            if (!rel.TryGetValue("related_to", out var t) || t is null) { added.Add(rel); continue; }
            var loc = ParseLocator(t);
            if (loc is null) { added.Add(rel); continue; }
            if (!prevKeys.Contains(LocatorKey(loc))) added.Add(rel);
        }
        return added.Count > 0 ? added : null;
    }

    private static string LocatorKey(Locator l)
        => $"{JsonbHelpers.EnumMember(l.Type)}|{l.SpaceName}|{l.Subpath}|{l.Shortname}";

    // Static field-sets for the two dispatchers that need to bypass
    // ApplyPatch's restricted-field gate. Naming a set per dispatcher is
    // clearer than a global bool — each call site declares exactly which
    // restricted fields it intends to mutate, and accidental copy-paste
    // can't open more than that.
    public static readonly IReadOnlySet<string> AssignRestrictedFields =
        new HashSet<string>(StringComparer.Ordinal) { "owner_shortname" };
    public static readonly IReadOnlySet<string> UpdateAclRestrictedFields =
        new HashSet<string>(StringComparer.Ordinal) { "acl" };

    public async Task<Result<Entry>> UpdateAsync(
        Locator locator,
        Dictionary<string, object> patch,
        string? actor,
        CancellationToken ct = default,
        // The set of restricted fields this caller is allowed to mutate.
        // null/empty preserves all restricted fields (regular update path).
        // ApplyPatch only consults this for fields in Meta.restricted_fields
        // (currently `owner_shortname`, `acl`); regular fields ignore it.
        IReadOnlySet<string>? allowedRestrictedFields = null,
        // When non-null, the permission walk gates on this action instead
        // of "update". Used by RequestType.assign (Python parity:
        // serve_request_assign requires the `assign` action) and reusable
        // for any future dispatcher that needs a non-update action gate.
        string? actionOverride = null,
        // Mirrors CreateAsync's `isBulkImport` — tags the emitted Event so
        // logging-only hooks (AuditPlugin) skip per-row noise. Used by
        // CsvService.ImportAsync's update branch to keep a 10k-row CSV
        // from generating 10k audit history rows.
        bool isBulkImport = false)
    {
        // Load existing first so the permission check has the resource context for
        // "own"/"is_active" conditions and the patch dict for field-restriction gating.
        var existing = await entries.GetAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, ct);
        if (existing is null)
            return Result<Entry>.Fail(InternalErrorCode.OBJECT_NOT_FOUND, "entry missing", ErrorTypes.Db);
        var action = actionOverride ?? "update";
        if (!await perms.CanAsync(actor, action, locator, PermissionService.FromEntry(existing), patch, ct))
            return Result<Entry>.Fail(InternalErrorCode.NOT_ALLOWED, $"no {action} access", ErrorTypes.Auth);

        var merged = ApplyPatch(existing, patch, allowedRestrictedFields);

        // Validate the MERGED entry (not the old one) so patches can't bypass schema rules.
        var validationError = await ValidatePayloadAsync(merged, ct);
        if (validationError is not null)
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, validationError, ErrorTypes.Request);

        // Same RI gate as Create, but scoped to relationships the patch
        // ADDS — refs that already existed pre-patch get a pass even if
        // their targets have since vanished. Without that scoping, deleting
        // a content entry would silently weaponize every existing entry
        // that still points at it: their next unrelated update (a tag bump,
        // a displayname tweak) would re-run the gate over stale rows and
        // fail. We catch typos at insert time while leaving historical
        // dangling refs to be cleaned up explicitly, not via accidental
        // breakage.
        var addedRels = DiffNewRelationships(existing.Relationships, merged.Relationships);
        var relError = await ValidateRelationshipsAsync(addedRels, ct);
        if (relError is not null)
            return Result<Entry>.Fail(InternalErrorCode.INVALID_DATA, relError, ErrorTypes.Request);

        // Folder-level compound-key uniqueness — same as Create, but the
        // existing entry is excluded from the conflict set (matching
        // Python's adapter.py::validate_uniqueness behavior on update).
        if (uniqueness is not null)
        {
            var uniqRes = await uniqueness.ValidateAsync(merged, ActionType.Update, existing, ct);
            if (!uniqRes.IsOk)
                return Result<Entry>.Fail(uniqRes.ErrorCode, uniqRes.ErrorMessage!, uniqRes.ErrorType!);
        }

        var beforeEvent = BuildEvent(merged, ActionType.Update, actor, isBulkImport);
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
        var historyDiff = HistoryDiffUtil.ComputeEntryDiff(existing, merged);
        await history.AppendAsync(locator.SpaceName, locator.Subpath, locator.Shortname, actor, null,
            historyDiff.Count > 0 ? historyDiff : null, ct);

        var afterEvent = BuildEvent(merged, ActionType.Update, actor, isBulkImport);
        if (historyDiff.Count > 0) afterEvent.Attributes["history_diff"] = historyDiff;
        await plugins.AfterActionAsync(afterEvent, ct);
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
            return Result<bool>.Fail(InternalErrorCode.NOT_ALLOWED, "no delete access", ErrorTypes.Auth);

        // Reverse referential-integrity: deleting an entry that other entries
        // still point at would leave dangling refs behind. Block with a
        // diagnostic that names the first blocker so the caller can decide to
        // fix-up or force the chain manually. Folder cascade is intentionally
        // skipped — auditing every descendant for external incoming refs is
        // structurally different (would need a subtree-aware query) and the
        // current users.delete-a-folder flow already accepts internal-only
        // refs disappearing along with the folder. Filed as a follow-up if
        // anyone needs symmetric folder-scoped checking.
        if (existing is not null && locator.Type != ResourceType.Folder)
        {
            var referencer = await entries.FindFirstReferencerAsync(
                locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type,
                excludeSpace: locator.SpaceName, excludeSubpath: locator.Subpath,
                excludeShortname: locator.Shortname, ct);
            if (referencer is { } r)
                return Result<bool>.Fail(InternalErrorCode.CANNT_DELETE,
                    $"entry has incoming relationships from {r.SpaceName}{r.Subpath}/{r.Shortname}",
                    ErrorTypes.Request);
        }

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

        // Python parity: deleting a folder cascades — the folder row plus
        // every descendant entry, attachment, history and lock row inside the
        // subtree disappears atomically. Mirrors adapter.py:2677-2696.
        // EntryRepository.DeleteFolderTreeWithDependentsAsync runs all five
        // DELETEs in one transaction so a partial failure rolls back instead
        // of leaving the DB half-deleted.
        bool ok;
        if (locator.Type == ResourceType.Folder)
        {
            var deletedRows = await entries.DeleteFolderTreeWithDependentsAsync(
                locator.SpaceName, locator.Subpath, locator.Shortname, ct);
            ok = deletedRows > 0;
        }
        else
        {
            ok = await entries.DeleteAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, ct);
            if (ok)
            {
                // A non-folder entry's attachments live at "{entry.subpath}/{entry.shortname}".
                // Match Python adapter.py:2769-2775.
                var entryPath = locator.Subpath == "/"
                    ? "/" + locator.Shortname
                    : locator.Subpath + "/" + locator.Shortname;
                await attachments.DeleteUnderSubpathAsync(locator.SpaceName, entryPath, ct);
            }
        }

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

        int totalMoved;
        try
        {
            totalMoved = await entries.MoveAsync(srcEntry, to, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // UNIQUE-violation at the destination: an entry or attachment with
            // the same (shortname, space_name, subpath) already exists there.
            // The repository's transaction rolled back, so the source is intact.
            return Result<Entry>.Fail(InternalErrorCode.SHORTNAME_ALREADY_EXIST,
                "destination already occupied", ErrorTypes.Db);
        }
        if (totalMoved == 0)
        {
            // Concurrent delete between load and write: the source row no
            // longer matches by uuid. Nothing was relocated; surface NOT_FOUND
            // explicitly rather than falling through to the post-move re-fetch.
            return Result<Entry>.Fail(InternalErrorCode.OBJECT_NOT_FOUND,
                "source entry no longer exists", ErrorTypes.Db);
        }
        // We diverge from Python's no-history-on-move parity: the move now
        // touches enough state — entry row, regenerated query_policies,
        // attachment relocation, folder-subtree cascade — that operators
        // benefit from an audit trail of who relocated what when. The diff
        // keeps the standard `{field: {old, new}}` shape so the
        // /managed/query?type=history response stays uniform.
        var moveDiff = new Dictionary<string, object>
        {
            ["space_name"] = new Dictionary<string, string> { ["old"] = from.SpaceName, ["new"] = to.SpaceName },
            ["subpath"]    = new Dictionary<string, string> { ["old"] = from.Subpath,   ["new"] = to.Subpath },
            ["shortname"]  = new Dictionary<string, string> { ["old"] = from.Shortname, ["new"] = to.Shortname },
        };
        await history.AppendAsync(to.SpaceName, to.Subpath, to.Shortname, actor,
                                   requestHeaders: null, diff: moveDiff, ct);
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
    private static Event BuildEvent(Entry entry, ActionType action, string? actor, bool isBulkImport = false)
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
            // Snapshot the resource fields Python's action_log captures inside
            // the `resource` (Locator) block — so the audit log can render the
            // same nested shape without re-fetching the entry.
            Uuid = entry.Uuid,
            Displayname = entry.Displayname,
            Description = entry.Description,
            Tags = entry.Tags,
            IsBulkImport = isBulkImport,
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

    private static Entry ApplyPatch(Entry existing, Dictionary<string, object> patch, IReadOnlySet<string>? allowedRestrictedFields)
    {
        bool RestrictedAllowed(string field) =>
            allowedRestrictedFields is not null && allowedRestrictedFields.Contains(field);

        // absent key → keep existing value; explicit JSON null (either CLR or
        // JsonElement(Null)) → clear; anything else → stringify. The earlier
        // shape `v is not null ? v.ToString() : fallback` silently wrote the
        // empty string for HTTP `"field": null` because JsonElement(Null) is
        // a struct value, not CLR null. See IsPatchNull.
        string? Str(string key, string? fallback)
        {
            if (!patch.TryGetValue(key, out var v)) return fallback;
            if (IsPatchNull(v)) return null;
            if (v is JsonElement el && el.ValueKind == JsonValueKind.String) return el.GetString();
            return v.ToString();
        }

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
            if (!patch.TryGetValue(key, out var v)) return fallback;
            // Explicit null clears the column. Falling through to fallback
            // would silently no-op on `"is_open": null`, which contradicts
            // the patch contract used by `relationships` / `displayname`.
            if (IsPatchNull(v)) return null;
            if (v is bool bv) return bv;
            if (v is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.True) return true;
                if (je.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }

        // System.Text.Json source-gen lands JSON arrays in Dictionary<string, object>
        // as JsonElement, not List<object> — so the prior `is IEnumerable<object>`
        // pattern silently dropped tag patches from HTTP callers.
        // `"tags": null` collapses to an empty list (the column is non-nullable
        // and defaults to []), matching the "null clears" patch contract.
        List<string> PatchTags(List<string> fallback)
        {
            if (!patch.TryGetValue("tags", out var raw)) return fallback;
            if (IsPatchNull(raw)) return new List<string>();
            if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in el.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                        list.Add(s);
                return list;
            }
            if (raw is List<string> sl) return sl;
            if (raw is IEnumerable<object> objs)
                return objs.Select(t => t?.ToString() ?? "").ToList();
            return fallback;
        }

        // Relationships are a full-list replacement on patch, not a deep
        // merge — clients that want to add one relationship send the whole
        // list. The wire shape lands as a JsonElement array (source-gen);
        // route through the same FromRelationships path MaterializeEntry
        // uses so the SQL writer sees identical structure regardless of
        // entry-point. `relationships: null` clears the column — IsPatchNull
        // covers both the in-process literal-null and HTTP JsonElement(Null).
        List<Dictionary<string, object>>? PatchRelationships()
        {
            if (!patch.TryGetValue("relationships", out var raw)) return existing.Relationships;
            if (IsPatchNull(raw)) return null;
            if (raw is List<Dictionary<string, object>> direct) return direct;
            if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
                return JsonbHelpers.FromRelationships(el.GetRawText());
            return existing.Relationships;
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
            // Python parity: `owner_shortname` is in Meta.restricted_fields,
            // so a regular UPDATE never reassigns ownership — it stays at
            // creation-time. Without this, the new `owner_shortname =
            // EXCLUDED.owner_shortname` clause in EntryRepository.UpsertAsync
            // would let any authenticated /managed/request caller transfer
            // ownership by including the field in their patch body.
            // RequestType.assign opts in via AssignRestrictedFields —
            // matches Python's serve_request_assign gating shape.
            OwnerShortname = (RestrictedAllowed("owner_shortname") && patch.ContainsKey("owner_shortname")
                ? Str("owner_shortname", existing.OwnerShortname)
                : existing.OwnerShortname) ?? existing.OwnerShortname,
            State = Str("state", existing.State),
            IsOpen = PatchBool("is_open", existing.IsOpen),
            WorkflowShortname = Str("workflow_shortname", existing.WorkflowShortname),
            ResolutionReason = Str("resolution_reason", existing.ResolutionReason),
            Tags = PatchTags(existing.Tags),
            // PatchBool returns bool? (since the IsOpen caller above needs a
            // nullable result); Entry.IsActive is non-nullable, so the ??
            // unwraps the always-non-null result back to bool. The analyzer
            // sees the fallback path through PatchBool always returning
            // non-null when given a non-null fallback and flags the ?? as
            // dead — keep it as a defensive belt-and-suspenders so a future
            // PatchBool refactor that introduces a null-returning branch
            // can't silently flip IsActive to false.
#pragma warning disable CA1508
            IsActive = PatchBool("is_active", existing.IsActive) ?? existing.IsActive,
#pragma warning restore CA1508
            // Python parity: `acl` lives in Meta.restricted_fields and is only
            // writable through the dedicated update_acl path. Regular update
            // ignores it; DispatchUpdateAclAsync opts in via
            // UpdateAclRestrictedFields.
            Acl = RestrictedAllowed("acl") && patch.ContainsKey("acl")
                ? ParsePatchAcl(patch)
                : existing.Acl,
            Relationships = PatchRelationships(),
            Payload = payload,
            UpdatedAt = TimeUtils.Now(),
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
