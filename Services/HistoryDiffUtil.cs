using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;

namespace Dmart.Services;

// Flatten + structural-equality helpers shared by EntryService, UserService,
// and the native-plugin callback bridge for building Python-parity history
// diffs ({field_path: {old, new}}).
internal static class HistoryDiffUtil
{
    public static void FlattenJson(JsonElement el, string prefix, Dictionary<string, object?> outDict)
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

    public static bool ValuesEqual(object? a, object? b)
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
    // objects so JSONB's canonicalization doesn't surface as a diff.
    public static bool JsonElementEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            // Allow int↔double when numerically equal — JSONB normalizes to a
            // single numeric representation; clients may send ints as either.
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

    // Python parity: history.diff is stored as {field_path: {old, new}}.
    // Single source of truth — EntryService.UpdateAsync and the native-plugin
    // SaveEntryCb both call through here so persisted history and
    // after_action's attributes.history_diff stay aligned.
    public static Dictionary<string, object> ComputeEntryDiff(Entry oldE, Entry newE)
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

    // {field_path: {old, new}} restricted to user-facing fields. Password
    // hash, AttemptCount, UpdatedAt are deliberately excluded to avoid
    // leaking secrets and noisy entries — UserService.UpdateProfileAsync and
    // the native-plugin UpdateUserCb both route through here.
    public static Dictionary<string, object> ComputeUserDiff(User oldU, User newU)
    {
        var oldFlat = FlattenUser(oldU);
        var newFlat = FlattenUser(newU);
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

    private static Dictionary<string, object?> FlattenUser(User u)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["email"] = u.Email,
            ["msisdn"] = u.Msisdn,
            // dmart's wire format for Language is the EnumMember string
            // ("english"/"arabic"/...), not the lowered C# enum name
            // ("en"/"ar"/...). Python's history diff carries the wire form,
            // so use JsonbHelpers.EnumMember to match.
            ["language"] = JsonbHelpers.EnumMember(u.Language),
            ["is_email_verified"] = u.IsEmailVerified,
            ["is_msisdn_verified"] = u.IsMsisdnVerified,
            ["force_password_change"] = u.ForcePasswordChange,
            ["device_id"] = u.DeviceId,
        };
        if (u.Displayname is not null)
        {
            d["displayname.en"] = u.Displayname.En;
            d["displayname.ar"] = u.Displayname.Ar;
            d["displayname.ku"] = u.Displayname.Ku;
        }
        if (u.Description is not null)
        {
            d["description.en"] = u.Description.En;
            d["description.ar"] = u.Description.Ar;
            d["description.ku"] = u.Description.Ku;
        }
        if (u.Payload?.Body is JsonElement body)
            FlattenJson(body, "payload.body", d);
        return d;
    }

    // ----- Role / Permission / Space diffs -----
    //
    // The User/Role/Permission/Space CRUD path on /managed/request did not
    // emit history rows before; entries went through EntryService which
    // already wrote diffs, but the dedicated repository branches in
    // RequestHandler.DispatchUpdateAsync called UpsertAsync straight without
    // computing a diff. These helpers + DiffFromMaps below let the dispatcher
    // build the same `{field: {old, new}}` shape clients already consume from
    // /managed/query?type=history for entries and self-service profile edits.

    public static Dictionary<string, object> ComputeRoleDiff(Role oldR, Role newR)
        => DiffFromMaps(FlattenRole(oldR), FlattenRole(newR));

    public static Dictionary<string, object> ComputePermissionDiff(Permission oldP, Permission newP)
        => DiffFromMaps(FlattenPermission(oldP), FlattenPermission(newP));

    public static Dictionary<string, object> ComputeSpaceDiff(Space oldS, Space newS)
        => DiffFromMaps(FlattenSpace(oldS), FlattenSpace(newS));

    private static Dictionary<string, object> DiffFromMaps(
        Dictionary<string, object?> oldFlat, Dictionary<string, object?> newFlat)
    {
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

    private static Dictionary<string, object?> FlattenMetasBase(
        bool isActive, string? slug, Translation? displayname, Translation? description,
        List<string>? tags, Payload? payload,
        string ownerShortname, string? ownerGroupShortname)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = isActive,
            ["slug"] = slug,
            ["tags"] = tags ?? new(),
            ["owner_shortname"] = ownerShortname,
            ["owner_group_shortname"] = ownerGroupShortname,
        };
        if (displayname is not null)
        {
            d["displayname.en"] = displayname.En;
            d["displayname.ar"] = displayname.Ar;
            d["displayname.ku"] = displayname.Ku;
        }
        if (description is not null)
        {
            d["description.en"] = description.En;
            d["description.ar"] = description.Ar;
            d["description.ku"] = description.Ku;
        }
        if (payload?.Body is JsonElement body)
            FlattenJson(body, "payload.body", d);
        return d;
    }

    // For list-valued flatten entries (Role.Permissions,
    // Permission.{ResourceTypes,Actions,Conditions,RestrictedFields},
    // Space.{Mirrors,HideFolders,Languages}) the emitted diff surfaces the
    // *entire* list before/after on any change, not the specific item(s)
    // added or removed. Consumers compute the actual delta if they need it.
    // Item-level deltas would require a different storage shape (multi-key
    // or array-index path) and aren't what the current /managed/query?type=history
    // contract promises — Python parity, intentional.
    private static Dictionary<string, object?> FlattenRole(Role r)
    {
        var d = FlattenMetasBase(r.IsActive, r.Slug, r.Displayname, r.Description, r.Tags, r.Payload,
            r.OwnerShortname, r.OwnerGroupShortname);
        d["permissions"] = r.Permissions;
        return d;
    }

    private static Dictionary<string, object?> FlattenPermission(Permission p)
    {
        var d = FlattenMetasBase(p.IsActive, p.Slug, p.Displayname, p.Description, p.Tags, p.Payload,
            p.OwnerShortname, p.OwnerGroupShortname);
        // Subpaths is Dict<string, List<string>>; flatten one level so a
        // changed entry surfaces as e.g. `subpaths./content: {old, new}`
        // rather than dumping the entire nested map under one key.
        foreach (var (k, v) in p.Subpaths)
            d[$"subpaths.{k}"] = v ?? new();
        d["resource_types"] = p.ResourceTypes;
        d["actions"] = p.Actions;
        d["conditions"] = p.Conditions;
        d["restricted_fields"] = p.RestrictedFields ?? new();
        // AllowedFieldsValues is Dict<string, object>; flatten one level
        // mirroring Subpaths so a constraint change on a single field
        // (e.g. allowed_fields_values.state changing its allow-list) shows
        // up as one key in the diff rather than the whole map. This is
        // security-meaningful — these constraints are what `allowed_fields_values`
        // permissions use to gate writes, and audit needs to surface their
        // mutation.
        foreach (var (k, v) in p.AllowedFieldsValues ?? new())
            d[$"allowed_fields_values.{k}"] = v;
        return d;
    }

    private static Dictionary<string, object?> FlattenSpace(Space s)
    {
        var d = FlattenMetasBase(s.IsActive, s.Slug, s.Displayname, s.Description, s.Tags, s.Payload,
            s.OwnerShortname, s.OwnerGroupShortname);
        d["root_registration_signature"] = s.RootRegistrationSignature;
        d["primary_website"] = s.PrimaryWebsite;
        d["indexing_enabled"] = s.IndexingEnabled;
        d["capture_misses"] = s.CaptureMisses;
        d["check_health"] = s.CheckHealth;
        d["languages"] = (s.Languages ?? new()).Select(JsonbHelpers.EnumMember).ToList();
        d["icon"] = s.Icon;
        d["mirrors"] = s.Mirrors ?? new();
        d["hide_folders"] = s.HideFolders ?? new();
        d["hide_space"] = s.HideSpace;
        d["ordinal"] = s.Ordinal;
        return d;
    }
}
