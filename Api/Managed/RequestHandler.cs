using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Managed;

// /managed/request — dmart's unified CRUD endpoint. The body is a Request envelope
// containing a list of Records. We dispatch each record to the right repository
// based on its resource_type:
//
//   * User                                  → UserRepository (users table)
//   * Role                                  → AccessRepository (roles table)
//   * Permission                            → AccessRepository (permissions table)
//   * Space                                 → SpaceRepository (spaces table)
//   * Comment/Reply/Reaction/Media/Json/    → AttachmentRepository (attachments table)
//     Share/Lock/DataAsset/Relationship/
//     Alteration
//   * everything else (Content/Folder/      → EntryRepository via EntryService
//     Schema/Ticket/...)
public static class RequestHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/request",
            async Task<Response> (Request req, EntryService entries, UserRepository users,
                                  AccessRepository access, SpaceRepository spaces,
                                  AttachmentRepository attachments, PasswordHasher hasher,
                                  Plugins.PluginManager plugins,
                                  IOptions<DmartSettings> dmartSettings,
                                  HttpContext http, CancellationToken ct) =>
            {
                var actor = http.User.Identity?.Name ?? "anonymous";
                var managementSpace = dmartSettings.Value.ManagementSpace;
                var responses = new List<Record>();
                // Python parity: don't bail on the first failure — try every
                // record and aggregate failures. Python's shape (utils.py:serve_request_delete):
                //   failed_records.append({"record": shortname, "error": msg, "error_code": code})
                // On any failure, router.py emits 400 with code=SOMETHING_WRONG
                // and info=[{successfull: [...], failed: [...]}] (typo preserved).
                var failedRecords = new List<Dictionary<string, object>>();

                foreach (var rec in req.Records)
                {
                    var result = req.RequestType switch
                    {
                        RequestType.Create =>
                            await DispatchCreateAsync(rec, req.SpaceName, actor,
                                entries, users, access, spaces, attachments, hasher, ct),
                        RequestType.Update or RequestType.Patch =>
                            await DispatchUpdateAsync(rec, req.SpaceName, actor,
                                entries, users, access, spaces, attachments, hasher, ct),
                        RequestType.Delete =>
                            await DispatchDeleteAsync(rec, req.SpaceName, actor, managementSpace,
                                entries, users, access, spaces, attachments, ct),
                        RequestType.Move =>
                            await DispatchMoveAsync(rec, req.SpaceName, actor, entries, ct),
                        // Python: Assign sets collaborators on an entry.
                        RequestType.Assign =>
                            await DispatchAssignAsync(rec, req.SpaceName, actor, entries, ct),
                        // Python: UpdateAcl sets the per-entry ACL.
                        RequestType.UpdateAcl =>
                            await DispatchUpdateAclAsync(rec, req.SpaceName, actor, entries, ct),
                        _ =>
                            ((Response Response, Record UpdatedRecord))(Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                                $"{req.RequestType} not supported", "request"),
                             rec),
                    };

                    if (result.Response.Status != Status.Success)
                    {
                        failedRecords.Add(new Dictionary<string, object>
                        {
                            ["record"] = rec.Shortname,
                            ["error"] = result.Response.Error?.Message ?? "unknown error",
                            ["error_code"] = result.Response.Error?.Code ?? InternalErrorCode.SOMETHING_WRONG,
                        });
                        continue;
                    }
                    responses.Add(result.UpdatedRecord);

                    // Fire after-action plugin hooks for non-entry CRUD types.
                    // EntryService already fires hooks for entry creates/updates/deletes,
                    // but User/Role/Permission/Space go through their own repositories
                    // and bypass EntryService. Python fires plugins for all of these.
                    if (rec.ResourceType is ResourceType.User or ResourceType.Role
                        or ResourceType.Permission or ResourceType.Space)
                    {
                        // Invalidate the space cache so AfterAction hooks see
                        // the freshly created/updated space's active_plugins.
                        if (rec.ResourceType is ResourceType.Space)
                            plugins.InvalidateSpaceCache(rec.Shortname);

                        var actionType = req.RequestType switch
                        {
                            RequestType.Create => ActionType.Create,
                            RequestType.Update or RequestType.Patch => ActionType.Update,
                            RequestType.Delete => ActionType.Delete,
                            RequestType.Move => ActionType.Move,
                            _ => (ActionType?)null,
                        };
                        if (actionType is not null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await plugins.AfterActionAsync(new Models.Core.Event
                                    {
                                        SpaceName = req.SpaceName,
                                        Subpath = rec.Subpath,
                                        Shortname = rec.Shortname,
                                        ActionType = actionType.Value,
                                        ResourceType = rec.ResourceType,
                                        UserShortname = actor,
                                    });
                                }
                                catch (Exception ex) { Console.Error.WriteLine($"WARN: after-action plugin error: {ex.Message}"); }
                            }, CancellationToken.None);
                        }
                    }
                }

                if (failedRecords.Count == 0) return Response.Ok(responses);

                // Python aggregate failure: HTTP 400 with info=[{successfull, failed}].
                // The "successfull" typo is preserved — clients branch on that key.
                var info = new List<Dictionary<string, object>>
                {
                    new()
                    {
                        ["successfull"] = responses,
                        ["failed"] = failedRecords,
                    },
                };
                return Response.Fail(InternalErrorCode.SOMETHING_WRONG,
                    "Something went wrong", "request", info);
            });

    // ============================================================================
    // CREATE
    // ============================================================================

    // Python: settings.auto_uuid_rule = "auto". When shortname == "auto",
    // generate a UUID and use the first 8 chars as the shortname.
    private const string AutoShortname = "auto";

    internal static Record ResolveAutoShortname(Record rec)
    {
        if (!string.Equals(rec.Shortname, AutoShortname, StringComparison.OrdinalIgnoreCase))
            return rec;
        var uuid = Guid.NewGuid();
        return rec with { Shortname = uuid.ToString("N")[..8], Uuid = uuid.ToString() };
    }

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchCreateAsync(
        Record rec, string space, string actor,
        EntryService entries, UserRepository users, AccessRepository access,
        SpaceRepository spaces, AttachmentRepository attachments, PasswordHasher hasher,
        CancellationToken ct)
    {
        rec = ResolveAutoShortname(rec);
        switch (rec.ResourceType)
        {
            case ResourceType.User:
                return await CreateUserAsync(rec, space, actor, users, hasher, ct);
            case ResourceType.Role:
                return await CreateRoleAsync(rec, space, actor, access, ct);
            case ResourceType.Permission:
                return await CreatePermissionAsync(rec, space, actor, access, ct);
            case ResourceType.Space:
                return await CreateSpaceAsync(rec, actor, spaces, ct);
            case ResourceType.Comment:
            case ResourceType.Reply:
            case ResourceType.Reaction:
            case ResourceType.Media:
            case ResourceType.Json:
            case ResourceType.Share:
            case ResourceType.Relationship:
            case ResourceType.Alteration:
            case ResourceType.Lock:
            case ResourceType.DataAsset:
                return await CreateAttachmentAsync(rec, space, actor, attachments, ct);
            default:
                return await CreateEntryAsync(rec, space, actor, entries, ct);
        }
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateEntryAsync(
        Record rec, string space, string actor, EntryService entries, CancellationToken ct)
    {
        var entry = MaterializeEntry(rec, space, actor);
        // Python parity: pass record.attributes (client-supplied) to the create
        // gate, not the synthesized Entry dict. Synthesizing is_active/tags on
        // the gate side makes restricted_fields:["is_active"]/["tags"] deny
        // creates the Python port allows for the same request body.
        var result = await entries.CreateAsync(entry, actor, rec.Attributes, ct);
        return result.IsOk
            ? (Response.Ok(), rec with { Uuid = result.Value!.Uuid })
            : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateUserAsync(
        Record rec, string space, string actor, UserRepository users, PasswordHasher hasher, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var existing = await users.GetByShortnameAsync(rec.Shortname, ct);
        if (existing is not null)
            return (Response.Fail(InternalErrorCode.SHORTNAME_ALREADY_EXIST,
                $"user {rec.Shortname} already exists", "request"), rec);

        var rolesList = ExtractStringList(attrs, "roles");
        var groupsList = ExtractStringList(attrs, "groups");
        var languageStr = attrs.TryGetValue("language", out var lObj) ? ConvertToString(lObj) : null;
        var typeStr = attrs.TryGetValue("type", out var tObj) ? ConvertToString(tObj) : null;
        var passwordRaw = attrs.TryGetValue("password", out var pObj) ? ConvertToString(pObj) : null;

        var user = new Dmart.Models.Core.User
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = space,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            OwnerShortname = actor,
            Email = attrs.TryGetValue("email", out var e) ? ConvertToString(e) : null,
            Msisdn = attrs.TryGetValue("msisdn", out var m) ? ConvertToString(m) : null,
            Password = string.IsNullOrEmpty(passwordRaw) ? null : hasher.Hash(passwordRaw),
            Roles = rolesList ?? new(),
            Groups = groupsList ?? new(),
            Type = ParseUserType(typeStr),
            Language = ParseLanguage(languageStr),
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            IsEmailVerified = attrs.TryGetValue("is_email_verified", out var iev) && IsTruthy(iev),
            IsMsisdnVerified = attrs.TryGetValue("is_msisdn_verified", out var imv) && IsTruthy(imv),
            ForcePasswordChange = attrs.TryGetValue("force_password_change", out var fpc) && IsTruthy(fpc),
            // Python accepts device_id / locked_to_device on user create; mirror
            // so mobile clients can set their device fingerprint in one call
            // instead of create + update.
            DeviceId = attrs.TryGetValue("device_id", out var did) ? ConvertToString(did) : null,
            LockedToDevice = attrs.TryGetValue("locked_to_device", out var ltd) && IsTruthy(ltd),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user, ct);
        return (Response.Ok(), rec with { Uuid = user.Uuid });
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateRoleAsync(
        Record rec, string space, string actor, AccessRepository access, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var role = new Role
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = space,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            OwnerShortname = actor,
            Permissions = ExtractStringList(attrs, "permissions") ?? new(),
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await access.UpsertRoleAsync(role, ct);
        return (Response.Ok(), rec with { Uuid = role.Uuid });
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreatePermissionAsync(
        Record rec, string space, string actor, AccessRepository access, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var perm = new Permission
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = space,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            OwnerShortname = actor,
            Subpaths = ExtractSubpathsDict(attrs),
            ResourceTypes = ExtractStringList(attrs, "resource_types") ?? new(),
            Actions = ExtractStringList(attrs, "actions") ?? new(),
            Conditions = ExtractStringList(attrs, "conditions") ?? new(),
            RestrictedFields = ExtractStringList(attrs, "restricted_fields"),
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await access.UpsertPermissionAsync(perm, ct);
        return (Response.Ok(), rec with { Uuid = perm.Uuid });
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateSpaceAsync(
        Record rec, string actor, SpaceRepository spaces, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var existing = await spaces.GetAsync(rec.Shortname, ct);
        if (existing is not null)
            return (Response.Fail(InternalErrorCode.ALREADY_EXIST_SPACE_NAME,
                $"space {rec.Shortname} already exists", "request"), rec);

        // Python's Meta.from_record passes ALL attributes to the Space constructor,
        // including active_plugins. Without active_plugins, no hooks fire for this
        // space — the resource_folders_creation plugin won't create the /schema folder.
        var activePlugins = ExtractStringList(attrs, "active_plugins")
            ?? new() { "resource_folders_creation", "audit" };

        var space = new Space
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = rec.Shortname,    // self-referential — every space row's space_name = shortname
            Subpath = "/",
            OwnerShortname = actor,
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            HideSpace = attrs.TryGetValue("hide_space", out var hs) ? IsTruthy(hs) : null,
            IndexingEnabled = attrs.TryGetValue("indexing_enabled", out var ie) && IsTruthy(ie),
            Languages = new() { Language.En },
            ActivePlugins = activePlugins,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await spaces.UpsertAsync(space, ct);
        return (Response.Ok(), rec with { Uuid = space.Uuid });
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateAttachmentAsync(
        Record rec, string space, string actor, AttachmentRepository attachments, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var attachment = new Attachment
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = space,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            ResourceType = rec.ResourceType,
            OwnerShortname = actor,
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            Body = attrs.TryGetValue("body", out var b) ? ConvertToString(b) : null,
            State = attrs.TryGetValue("state", out var s) ? ConvertToString(s) : null,
            CreatedAt = DateTime.UtcNow,
        };
        await attachments.UpsertAsync(attachment, ct);
        return (Response.Ok(), rec with { Uuid = attachment.Uuid });
    }

    // ============================================================================
    // UPDATE
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchUpdateAsync(
        Record rec, string space, string actor,
        EntryService entries, UserRepository users, AccessRepository access,
        SpaceRepository spaces, AttachmentRepository attachments, PasswordHasher hasher,
        CancellationToken ct)
    {
        var locator = new Locator(rec.ResourceType, space, rec.Subpath, rec.Shortname);

        switch (rec.ResourceType)
        {
            case ResourceType.User:
            {
                var existing = await users.GetByShortnameAsync(rec.Shortname, ct);
                if (existing is null)
                    return (Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user not found", "request"), rec);
                var attrs = rec.Attributes ?? new();
                var passwordRaw = attrs.TryGetValue("password", out var p) ? ConvertToString(p) : null;
                var newIsActive = attrs.TryGetValue("is_active", out var ia) ? !IsExplicitlyFalse(ia) : existing.IsActive;
                var reactivating = !existing.IsActive && newIsActive;
                var updated = existing with
                {
                    Email = attrs.TryGetValue("email", out var e) ? ConvertToString(e) : existing.Email,
                    Msisdn = attrs.TryGetValue("msisdn", out var m) ? ConvertToString(m) : existing.Msisdn,
                    Password = string.IsNullOrEmpty(passwordRaw) ? existing.Password : hasher.Hash(passwordRaw),
                    Roles = ExtractStringList(attrs, "roles") ?? existing.Roles,
                    Groups = ExtractStringList(attrs, "groups") ?? existing.Groups,
                    Tags = ExtractStringList(attrs, "tags") ?? existing.Tags,
                    Slug = attrs.TryGetValue("slug", out var sl) ? ConvertToString(sl) : existing.Slug,
                    Displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : existing.Displayname,
                    Description = attrs.TryGetValue("description", out var desc) ? ParseTranslation(desc) : existing.Description,
                    IsActive = newIsActive,
                    AttemptCount = reactivating ? 0 : existing.AttemptCount,
                    IsEmailVerified = attrs.TryGetValue("is_email_verified", out var iev) ? IsTruthy(iev) : existing.IsEmailVerified,
                    IsMsisdnVerified = attrs.TryGetValue("is_msisdn_verified", out var imv) ? IsTruthy(imv) : existing.IsMsisdnVerified,
                    // Python writes device_id through on user update when present
                    // in the attributes block — match that so the persisted value
                    // stays in sync with the mobile client.
                    DeviceId = attrs.TryGetValue("device_id", out var did) ? ConvertToString(did) : existing.DeviceId,
                    LockedToDevice = attrs.TryGetValue("locked_to_device", out var ltd) ? IsTruthy(ltd) : existing.LockedToDevice,
                    UpdatedAt = DateTime.UtcNow,
                };
                await users.UpsertAsync(updated, ct);
                return (Response.Ok(), rec with { Uuid = updated.Uuid });
            }
            case ResourceType.Space:
            {
                var existing = await spaces.GetAsync(rec.Shortname, ct);
                if (existing is null)
                    return (Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "space not found", "request"), rec);
                var attrs = rec.Attributes ?? new();

                // Python parity: every Space-specific attribute on the wire is
                // written through. Previously only hide_space + is_active were
                // patched, so frontend "save space settings" silently dropped
                // icon/primary_website/languages/active_plugins/etc.
                var languages = attrs.TryGetValue("languages", out var lgRaw)
                    ? ExtractLanguages(lgRaw) ?? existing.Languages
                    : existing.Languages;

                var updated = existing with
                {
                    // Metas base fields — the frontend form posts all of these.
                    Slug = attrs.TryGetValue("slug", out var sl) ? ConvertToString(sl) : existing.Slug,
                    Displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : existing.Displayname,
                    Description = attrs.TryGetValue("description", out var desc) ? ParseTranslation(desc) : existing.Description,
                    Tags = ExtractStringList(attrs, "tags") ?? existing.Tags,
                    IsActive = attrs.TryGetValue("is_active", out var ia) ? !IsExplicitlyFalse(ia) : existing.IsActive,

                    // Space-specific fields.
                    RootRegistrationSignature = attrs.TryGetValue("root_registration_signature", out var rrs)
                        ? (ConvertToString(rrs) ?? "") : existing.RootRegistrationSignature,
                    PrimaryWebsite = attrs.TryGetValue("primary_website", out var pw)
                        ? (ConvertToString(pw) ?? "") : existing.PrimaryWebsite,
                    IndexingEnabled = attrs.TryGetValue("indexing_enabled", out var ie) ? IsTruthy(ie) : existing.IndexingEnabled,
                    CaptureMisses = attrs.TryGetValue("capture_misses", out var cm) ? IsTruthy(cm) : existing.CaptureMisses,
                    CheckHealth = attrs.TryGetValue("check_health", out var ch) ? IsTruthy(ch) : existing.CheckHealth,
                    Languages = languages,
                    Icon = attrs.TryGetValue("icon", out var ic) ? (ConvertToString(ic) ?? "") : existing.Icon,
                    Mirrors = attrs.ContainsKey("mirrors") ? ExtractStringList(attrs, "mirrors") : existing.Mirrors,
                    HideFolders = attrs.ContainsKey("hide_folders") ? ExtractStringList(attrs, "hide_folders") : existing.HideFolders,
                    HideSpace = attrs.TryGetValue("hide_space", out var hs) ? IsTruthy(hs) : existing.HideSpace,
                    ActivePlugins = attrs.ContainsKey("active_plugins") ? ExtractStringList(attrs, "active_plugins") : existing.ActivePlugins,
                    Ordinal = attrs.TryGetValue("ordinal", out var ord) ? ParseInt(ord) ?? existing.Ordinal : existing.Ordinal,

                    UpdatedAt = DateTime.UtcNow,
                };
                await spaces.UpsertAsync(updated, ct);
                return (Response.Ok(), rec with { Uuid = updated.Uuid });
            }
            // Role/Permission live in their own tables, not `entries` — falling
            // through to EntryService.UpdateAsync would silently no-op.
            case ResourceType.Role:
            {
                var existing = await access.GetRoleAsync(rec.Shortname, ct);
                if (existing is null)
                    return (Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "role not found", "request"), rec);
                var attrs = rec.Attributes ?? new();
                var updated = existing with
                {
                    Permissions = ExtractStringList(attrs, "permissions") ?? existing.Permissions,
                    Tags = ExtractStringList(attrs, "tags") ?? existing.Tags,
                    Slug = attrs.TryGetValue("slug", out var sl) ? ConvertToString(sl) : existing.Slug,
                    Displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : existing.Displayname,
                    Description = attrs.TryGetValue("description", out var desc) ? ParseTranslation(desc) : existing.Description,
                    IsActive = attrs.TryGetValue("is_active", out var ia) ? !IsExplicitlyFalse(ia) : existing.IsActive,
                    UpdatedAt = DateTime.UtcNow,
                };
                await access.UpsertRoleAsync(updated, ct);
                return (Response.Ok(), rec with { Uuid = updated.Uuid });
            }
            case ResourceType.Permission:
            {
                var existing = await access.GetPermissionAsync(rec.Shortname, ct);
                if (existing is null)
                    return (Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "permission not found", "request"), rec);
                var attrs = rec.Attributes ?? new();
                var updated = existing with
                {
                    // Subpaths has no "missing" form: ExtractSubpathsDict returns
                    // empty when absent, which would clobber. Only replace when
                    // `subpaths` is explicitly present on the wire.
                    Subpaths = attrs.ContainsKey("subpaths") ? ExtractSubpathsDict(attrs) : existing.Subpaths,
                    ResourceTypes = ExtractStringList(attrs, "resource_types") ?? existing.ResourceTypes,
                    Actions = ExtractStringList(attrs, "actions") ?? existing.Actions,
                    Conditions = ExtractStringList(attrs, "conditions") ?? existing.Conditions,
                    RestrictedFields = attrs.ContainsKey("restricted_fields")
                        ? ExtractStringList(attrs, "restricted_fields")
                        : existing.RestrictedFields,
                    Tags = ExtractStringList(attrs, "tags") ?? existing.Tags,
                    Slug = attrs.TryGetValue("slug", out var sl) ? ConvertToString(sl) : existing.Slug,
                    Displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : existing.Displayname,
                    Description = attrs.TryGetValue("description", out var desc) ? ParseTranslation(desc) : existing.Description,
                    IsActive = attrs.TryGetValue("is_active", out var ia) ? !IsExplicitlyFalse(ia) : existing.IsActive,
                    UpdatedAt = DateTime.UtcNow,
                };
                await access.UpsertPermissionAsync(updated, ct);
                return (Response.Ok(), rec with { Uuid = updated.Uuid });
            }
            default:
            {
                var result = await entries.UpdateAsync(locator, rec.Attributes ?? new(), actor, ct);
                return result.IsOk
                    ? (Response.Ok(), rec with { Uuid = result.Value!.Uuid })
                    : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
            }
        }
    }

    // ============================================================================
    // DELETE
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchDeleteAsync(
        Record rec, string space, string actor, string managementSpace,
        EntryService entries, UserRepository users, AccessRepository access,
        SpaceRepository spaces, AttachmentRepository attachments, CancellationToken ct)
    {
        var locator = new Locator(rec.ResourceType, space, rec.Subpath, rec.Shortname);

        switch (rec.ResourceType)
        {
            case ResourceType.User:
                await users.DeleteAsync(rec.Shortname, ct);
                return (Response.Ok(), rec);
            case ResourceType.Space:
                // Python parity: serve_space_delete refuses to drop the
                // management space — it's where users/roles/permissions live.
                if (string.Equals(rec.Shortname, managementSpace, StringComparison.Ordinal))
                    return (Response.Fail(InternalErrorCode.CANNT_DELETE,
                        $"cannot delete the management space '{managementSpace}'", "request"), rec);
                await spaces.DeleteAsync(rec.Shortname, ct);
                return (Response.Ok(), rec);
            // Role/Permission live in their own tables, not `entries` — falling
            // through to EntryService.DeleteAsync would silently no-op.
            case ResourceType.Role:
            {
                var deleted = await access.DeleteRoleAsync(rec.Shortname, ct);
                return deleted
                    ? (Response.Ok(), rec)
                    : (Response.Fail(InternalErrorCode.OBJECT_NOT_FOUND,
                        $"role '{rec.Shortname}' not found", "request"), rec);
            }
            case ResourceType.Permission:
            {
                var deleted = await access.DeletePermissionAsync(rec.Shortname, ct);
                return deleted
                    ? (Response.Ok(), rec)
                    : (Response.Fail(InternalErrorCode.OBJECT_NOT_FOUND,
                        $"permission '{rec.Shortname}' not found", "request"), rec);
            }
            case ResourceType.Comment:
            case ResourceType.Reply:
            case ResourceType.Reaction:
            case ResourceType.Media:
            case ResourceType.Json:
            case ResourceType.Share:
            case ResourceType.Relationship:
            case ResourceType.Alteration:
            case ResourceType.DataAsset:
            {
                // Look up by (space, subpath, shortname) and delete by uuid
                var existing = await attachments.GetAsync(space, "/" + rec.Subpath.TrimStart('/'), rec.Shortname, ct);
                if (existing is null) return (Response.Ok(), rec); // already gone
                if (Guid.TryParse(existing.Uuid, out var u))
                    await attachments.DeleteAsync(u, ct);
                return (Response.Ok(), rec);
            }
            default:
            {
                var result = await entries.DeleteAsync(locator, actor, ct);
                return result.IsOk
                    ? (Response.Ok(), rec)
                    : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
            }
        }
    }

    // ============================================================================
    // MOVE
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchMoveAsync(
        Record rec, string space, string actor, EntryService entries, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        if (!TryGetString(attrs, "dest_subpath", out var destSubpath)
            || !TryGetString(attrs, "dest_shortname", out var destShortname))
            return (Response.Fail(InternalErrorCode.MISSING_DESTINATION_OR_SHORTNAME,
                "move requires dest_subpath and dest_shortname", "request"), rec);

        // Python supports cross-space move via src_space_name / dest_space_name.
        var srcSpace = TryGetString(attrs, "src_space_name", out var ss) && ss is not null ? ss : space;
        var destSpace = TryGetString(attrs, "dest_space_name", out var ds) && ds is not null ? ds : space;

        var locator = new Locator(rec.ResourceType, srcSpace, rec.Subpath, rec.Shortname);
        var to = new Locator(rec.ResourceType, destSpace, destSubpath!, destShortname!);
        var result = await entries.MoveAsync(locator, to, actor, ct);
        return result.IsOk
            ? (Response.Ok(), rec with { Subpath = to.Subpath, Shortname = to.Shortname })
            : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
    }

    // ============================================================================
    // ASSIGN (sets collaborators on an entry — Python: RequestType.assign)
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchAssignAsync(
        Record rec, string space, string actor, EntryService entries, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var collaborators = ExtractStringDict(attrs, "collaborators");
        var patch = new Dictionary<string, object>(StringComparer.Ordinal);
        if (collaborators is not null) patch["collaborators"] = collaborators;
        var locator = new Locator(rec.ResourceType, space, rec.Subpath, rec.Shortname);
        var result = await entries.UpdateAsync(locator, patch, actor, ct);
        return result.IsOk
            ? (Response.Ok(), rec with { Uuid = result.Value!.Uuid })
            : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
    }

    // ============================================================================
    // UPDATE_ACL (sets per-entry ACL — Python: RequestType.update_acl)
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchUpdateAclAsync(
        Record rec, string space, string actor, EntryService entries, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var patch = new Dictionary<string, object>(StringComparer.Ordinal);
        if (attrs.TryGetValue("acl", out var aclRaw)) patch["acl"] = aclRaw;
        var locator = new Locator(rec.ResourceType, space, rec.Subpath, rec.Shortname);
        var result = await entries.UpdateAsync(locator, patch, actor, ct);
        return result.IsOk
            ? (Response.Ok(), rec with { Uuid = result.Value!.Uuid })
            : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
    }

    // ============================================================================
    // helpers
    // ============================================================================

    internal static Entry MaterializeEntry(Record rec, string spaceName, string actor)
    {
        var attrs = rec.Attributes ?? new();
        // dmart stores schema_shortname inside the Payload object, not as an Entry column.
        Payload? payload = null;
        if (attrs.TryGetValue("payload", out var pRaw) && pRaw is JsonElement p && p.ValueKind == JsonValueKind.Object)
        {
            payload = new Payload
            {
                ContentType = ParseContentType(p.TryGetProperty("content_type", out var ct) ? ct.GetString() : null),
                SchemaShortname = p.TryGetProperty("schema_shortname", out var ss) ? ss.GetString() : null,
                Body = p.TryGetProperty("body", out var b) ? b.Clone() : null,
            };
        }
        else if (attrs.TryGetValue("schema_shortname", out var sc))
        {
            payload = new Payload { SchemaShortname = ConvertToString(sc) };
        }

        return new Entry
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = spaceName,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            ResourceType = rec.ResourceType,
            OwnerShortname = actor,
            OwnerGroupShortname = attrs.TryGetValue("owner_group_shortname", out var ogs) ? ConvertToString(ogs) : null,
            Slug = attrs.TryGetValue("slug", out var s) ? ConvertToString(s) : null,
            Displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : null,
            Description = attrs.TryGetValue("description", out var de) ? ParseTranslation(de) : null,
            Tags = ExtractStringList(attrs, "tags") ?? new(),
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            // Ticket-specific fields — Python reads these from record.attributes
            // via Meta.from_record → Ticket.__init__. C# was dropping them.
            State = attrs.TryGetValue("state", out var st) ? ConvertToString(st) : null,
            WorkflowShortname = attrs.TryGetValue("workflow_shortname", out var wf) ? ConvertToString(wf) : null,
            ResolutionReason = attrs.TryGetValue("resolution_reason", out var rr) ? ConvertToString(rr) : null,
            Collaborators = ExtractStringDict(attrs, "collaborators"),
            Reporter = ParseReporter(attrs),
            Payload = payload,
        };
    }

    private static Dictionary<string, string>? ExtractStringDict(Dictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var raw) || raw is null) return null;
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, string>();
            foreach (var prop in el.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString()!;
            return dict.Count > 0 ? dict : null;
        }
        return null;
    }

    private static Reporter? ParseReporter(Dictionary<string, object> attrs)
    {
        if (!attrs.TryGetValue("reporter", out var raw) || raw is null) return null;
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
            return new Reporter
            {
                Type = el.TryGetProperty("type", out var t) ? t.GetString() : null,
                Name = el.TryGetProperty("name", out var n) ? n.GetString() : null,
                Channel = el.TryGetProperty("channel", out var c) ? c.GetString() : null,
                Msisdn = el.TryGetProperty("msisdn", out var m) ? m.GetString() : null,
            };
        }
        return null;
    }

    private static ContentType ParseContentType(string? value)
        => value is null ? ContentType.Json
            : Enum.TryParse<ContentType>(value, true, out var ct) ? ct
            : value switch
            {
                "json" => ContentType.Json,
                "text" => ContentType.Text,
                "markdown" => ContentType.Markdown,
                "html" => ContentType.Html,
                _ => ContentType.Json,
            };

    private static UserType ParseUserType(string? code) => code?.ToLowerInvariant() switch
    {
        "mobile" => UserType.Mobile,
        "bot"    => UserType.Bot,
        _        => UserType.Web,
    };

    private static Language ParseLanguage(string? code) => code?.ToLowerInvariant() switch
    {
        "ar" or "arabic"  => Language.Ar,
        "ku" or "kurdish" => Language.Ku,
        "fr" or "french"  => Language.Fr,
        "tr" or "turkish" => Language.Tr,
        _                 => Language.En,
    };

    // Space.languages is posted as a string array, e.g. ["english", "ar", "ku"].
    // Map each entry through ParseLanguage so both short codes ("en") and long
    // names ("english") resolve. Returns null when the attribute is absent or
    // not an array, so the caller can fall back to the existing list.
    private static List<Language>? ExtractLanguages(object? raw)
    {
        var names = NormalizeStringArray(raw);
        if (names is null) return null;
        return names.Select(ParseLanguage).Distinct().ToList();
    }

    private static List<string>? NormalizeStringArray(object? raw)
    {
        if (raw is null) return null;
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            return list;
        }
        if (raw is List<string> sl) return sl;
        return null;
    }

    private static int? ParseInt(object? raw) => raw switch
    {
        null => null,
        int i => i,
        long l => (int)l,
        JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) => n,
        JsonElement el when el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s) => s,
        string ss when int.TryParse(ss, out var sv) => sv,
        _ => null,
    };

    private static Translation? ParseTranslation(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                return new Translation(
                    En: el.TryGetProperty("en", out var en) ? en.GetString() : null,
                    Ar: el.TryGetProperty("ar", out var ar) ? ar.GetString() : null,
                    Ku: el.TryGetProperty("ku", out var ku) ? ku.GetString() : null);
            }
            if (el.ValueKind == JsonValueKind.String)
                return new Translation(En: el.GetString());
        }
        return new Translation(En: value.ToString());
    }

    private static List<string>? ExtractStringList(Dictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var raw)) return null;
        if (raw is null) return null;
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            return list;
        }
        if (raw is List<string> stringList) return stringList;
        if (raw is IEnumerable<object> objs)
            return objs.Select(o => o?.ToString() ?? "").ToList();
        return null;
    }

    // Extracts the "subpaths" dictionary: { "space_name": ["subpath1", "subpath2"], ... }
    private static Dictionary<string, List<string>> ExtractSubpathsDict(Dictionary<string, object> attrs)
    {
        var result = new Dictionary<string, List<string>>();
        if (!attrs.TryGetValue("subpaths", out var raw) || raw is null) return result;

        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                var paths = new List<string>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in prop.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            paths.Add(item.GetString()!);
                result[prop.Name] = paths;
            }
        }
        return result;
    }

    private static string? ConvertToString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null   => null,
            _                    => el.GetRawText(),
        },
        _ => v.ToString(),
    };

    private static bool IsTruthy(object? v) => v switch
    {
        true => true,
        bool b => b,
        JsonElement el => el.ValueKind == JsonValueKind.True,
        _ => false,
    };

    private static bool IsExplicitlyFalse(object? v) => v switch
    {
        false => true,
        bool b => !b,
        JsonElement el => el.ValueKind == JsonValueKind.False,
        _ => false,
    };

    private static bool TryGetString(Dictionary<string, object> attrs, string key, out string? value)
    {
        value = null;
        if (!attrs.TryGetValue(key, out var raw)) return false;
        value = ConvertToString(raw);
        return value is not null;
    }
}
