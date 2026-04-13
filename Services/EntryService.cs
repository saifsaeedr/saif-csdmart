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
    SchemaValidator schemas)
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
            return Result<Entry>.Fail("forbidden", "no create access");

        var existing = await entries.GetAsync(entry.SpaceName, entry.Subpath, entry.Shortname, entry.ResourceType, ct);
        if (existing is not null) return Result<Entry>.Fail("conflict", "entry exists");

        // dmart's schema validation: if the payload references a schema_shortname,
        // load the schema entry from the same space and validate the body against it.
        var validationError = await ValidatePayloadAsync(entry, ct);
        if (validationError is not null) return Result<Entry>.Fail("invalid_data", validationError);

        var toSave = entry with
        {
            Uuid = string.IsNullOrEmpty(entry.Uuid) ? Guid.NewGuid().ToString() : entry.Uuid,
            OwnerShortname = string.IsNullOrEmpty(entry.OwnerShortname) ? (actor ?? "anonymous") : entry.OwnerShortname,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Fire the BEFORE hook before the DB write. A plugin throwing here should
        // abort the create — we translate the exception into a Result.Fail so the
        // caller can surface a structured error without leaking the stack.
        var beforeEvent = BuildEvent(toSave, ActionType.Create, actor);
        try { await plugins.BeforeActionAsync(beforeEvent, ct); }
        catch (Exception)
        {
            return Result<Entry>.Fail("bad_request", "plugin rejected create");
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
        if (existing is null) return Result<Entry>.Fail("not_found", "entry missing");
        if (!await perms.CanUpdateAsync(actor, locator, PermissionService.FromEntry(existing), patch, ct))
            return Result<Entry>.Fail("forbidden", "no update access");

        // If the patch updates the payload body, re-run schema validation.
        var validationError = await ValidatePayloadAsync(existing, ct);
        if (validationError is not null) return Result<Entry>.Fail("invalid_data", validationError);

        var merged = ApplyPatch(existing, patch);

        var beforeEvent = BuildEvent(merged, ActionType.Update, actor);
        try { await plugins.BeforeActionAsync(beforeEvent, ct); }
        catch (Exception)
        {
            return Result<Entry>.Fail("bad_request", "plugin rejected update");
        }

        await entries.UpsertAsync(merged, ct);
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
            return Result<bool>.Fail("forbidden", "no delete access");

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
        catch (Exception)
        {
            return Result<bool>.Fail("bad_request", "plugin rejected delete");
        }

        var ok = await entries.DeleteAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, ct);
        if (ok)
        {
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
        if (srcEntry is null) return Result<Entry>.Fail("not_found", "source entry missing");
        var srcCtx = PermissionService.FromEntry(srcEntry);
        if (!await perms.CanUpdateAsync(actor, from, srcCtx, null, ct) ||
            !await perms.CanCreateAsync(actor, to, EntryToAttributesDict(srcEntry), ct))
            return Result<Entry>.Fail("forbidden", "no move access");
        // Python fires a single "move" event keyed on the destination subpath and
        // passes the source shortname as an attribute. Mirror that so a hook like
        // ldap_manager can see both the old and new names.
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
        catch (Exception)
        {
            return Result<Entry>.Fail("bad_request", "plugin rejected move");
        }

        await entries.MoveAsync(from, to, ct);
        await history.AppendAsync(to.SpaceName, to.Subpath, to.Shortname, actor, null,
            new() { ["action"] = "move", ["from"] = $"{from.SpaceName}/{from.Subpath}/{from.Shortname}" }, ct);
        await plugins.AfterActionAsync(moveEvent, ct);
        var moved = await entries.GetAsync(to.SpaceName, to.Subpath, to.Shortname, to.Type, ct);
        return moved is null ? Result<Entry>.Fail("not_found", "moved entry missing") : Result<Entry>.Ok(moved);
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

        Translation? TrFromString(string key, Translation? fallback)
            => patch.TryGetValue(key, out var v) && v is not null ? new Translation(En: v.ToString()) : fallback;

        return existing with
        {
            Displayname = TrFromString("displayname", existing.Displayname),
            Description = TrFromString("description", existing.Description),
            Slug = Str("slug", existing.Slug),
            OwnerShortname = Str("owner_shortname", existing.OwnerShortname) ?? existing.OwnerShortname,
            State = Str("state", existing.State),
            WorkflowShortname = Str("workflow_shortname", existing.WorkflowShortname),
            Tags = patch.TryGetValue("tags", out var tagsRaw) && tagsRaw is IEnumerable<object> tags
                ? tags.Select(t => t?.ToString() ?? "").ToList() : existing.Tags,
            IsActive = patch.TryGetValue("is_active", out var ia) && ia is bool b ? b : existing.IsActive,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
