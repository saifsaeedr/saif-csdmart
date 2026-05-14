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
}
