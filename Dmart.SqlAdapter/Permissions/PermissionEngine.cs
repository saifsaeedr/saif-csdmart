using System.Collections.Concurrent;
using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.SqlAdapter.Helpers;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.SqlAdapter.Permissions;

// Port of Services/PermissionService.cs from the dmart server. Enforces the
// SAME role/permission/ACL contract that dmart's HTTP API enforces, so that
// a downstream ASP.NET project calling Dmart.SqlAdapter sees the same access
// outcomes as if it had gone through /managed/* endpoints.
//
// Surface:
//   - CanAsync(actor, action, target, resourceContext?, attrs?)
//     The single low-level gate. action is one of "view", "create", "update",
//     "delete", "query". target is the locator (space, subpath, shortname,
//     resource_type). resourceContext is the row being acted on when known
//     (lets the engine check "own" / "is_active" conditions). attrs is the
//     request body for create/update so field-level restrictions can fire.
//
//   - RequireAsync(...) — same shape but throws DmartPermissionDeniedException
//     when the answer is no. The adapter's methods use this so callers see
//     a clean exception rather than having to read a bool.
//
//   - BuildUserQueryPoliciesAsync(actor, space, subpath)
//     Builds the LIKE-pattern list that PermissionFilter feeds into the WHERE
//     clause of a paged query. An actor with no matching policies sees zero
//     rows from QueryAsync — same outcome as the API.
//
// Caching:
//   Resolved user permission sets are cached in-memory for 5 minutes. The
//   server has an invalidation-callback wired into role/permission writes;
//   we don't have that hook outside the server process, so we time-bound the
//   cache instead. Call InvalidateAll() if you've just edited roles or
//   permissions and need the change to apply immediately.
public sealed class PermissionEngine
{
    public const string AllSpacesMw      = "__all_spaces__";
    public const string AllSubpathsMw    = "__all_subpaths__";
    public const string CurrentUserMw    = "__current_user__";
    public const string CurrentUserOwnerMw = "__current_user__owner__";
    public const string AnonymousUser    = "anonymous";
    private const string ImplicitAuthenticatedRole = "logged_in";
    private const string WorldPermission = "world";

    private readonly DmartDb _db;
    private readonly JsonSerializerOptions _json;
    private readonly TimeSpan _cacheTtl;
    private readonly ConcurrentDictionary<string, CachedAccess> _cache = new(StringComparer.Ordinal);

    public PermissionEngine(DmartDb db, JsonSerializerOptions? json = null, TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _json = json ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);
    }

    public void InvalidateAll() => _cache.Clear();
    public void Invalidate(string actor) => _cache.TryRemove(actor, out _);

    // ===================================================================
    // Public surface
    // ===================================================================

    public async Task<bool> CanAsync(
        string? actor,
        string action,
        Locator target,
        ResourceContext? resource = null,
        Dictionary<string, object>? recordAttributes = null,
        CancellationToken ct = default)
    {
        actor ??= AnonymousUser;

        var (user, perms) = await ResolvePermissionsAsync(actor, ct).ConfigureAwait(false);
        if (actor != AnonymousUser && (user is null || !user.IsActive)) return false;

        // 1. Per-entry ACL — direct grant on the row wins immediately.
        if (resource?.Acl is { Count: > 0 } acls)
        {
            foreach (var entry in acls)
            {
                if (entry.UserShortname != actor) continue;
                if (entry.Denied is { Count: > 0 } denied
                    && denied.Contains(action, StringComparer.Ordinal))
                    return false;
                if (entry.AllowedActions is { Count: > 0 } allowed
                    && allowed.Contains(action, StringComparer.Ordinal))
                    return true;
            }
        }

        if (perms.Count == 0) return false;

        // 2. resource_achieved_conditions
        var achieved = new HashSet<string>(StringComparer.Ordinal);
        if (resource is not null)
        {
            if (resource.IsActive) achieved.Add("is_active");
            if (!string.IsNullOrEmpty(resource.OwnerShortname) && resource.OwnerShortname == actor)
                achieved.Add("own");
            else if (!string.IsNullOrEmpty(resource.OwnerGroupShortname)
                     && user?.Groups is { Count: > 0 } groups
                     && groups.Contains(resource.OwnerGroupShortname))
                achieved.Add("own");
        }

        var rt = EnumWire(target.Type);

        // Python: effective_space = shortname when resource_type is space.
        var effectiveSpace = target.Type == ResourceType.Space
            && !string.IsNullOrEmpty(target.Shortname) && target.Shortname != "*"
            ? target.Shortname
            : target.SpaceName;

        // For a folder check, the walk also includes the folder's own
        // shortname appended as a subpath segment.
        var walkPath = Locator.NormalizeSubpath(target.Subpath);
        if (target.Type == ResourceType.Folder
            && !string.IsNullOrEmpty(target.Shortname) && target.Shortname != "*")
        {
            walkPath = walkPath == "/"
                ? target.Shortname
                : $"{walkPath.TrimEnd('/')}/{target.Shortname}";
        }

        foreach (var candidate in BuildSubpathWalk(walkPath))
        {
            if (CheckAtCandidate(perms, effectiveSpace, candidate, rt, action, achieved, recordAttributes, actor))
                return true;
            var global = ToGlobalForm(candidate);
            if (global != candidate
                && CheckAtCandidate(perms, effectiveSpace, global, rt, action, achieved, recordAttributes, actor))
                return true;
        }

        return false;
    }

    public async Task RequireAsync(
        string? actor,
        string action,
        Locator target,
        ResourceContext? resource = null,
        Dictionary<string, object>? attrs = null,
        CancellationToken ct = default)
    {
        if (await CanAsync(actor, action, target, resource, attrs, ct).ConfigureAwait(false)) return;
        throw new DmartPermissionDeniedException(
            actor ?? AnonymousUser, action,
            target.SpaceName, target.Subpath, target.Shortname,
            EnumWire(target.Type));
    }

    public async Task<List<string>> BuildUserQueryPoliciesAsync(
        string? actor, string spaceName, string subpath, CancellationToken ct = default)
    {
        actor ??= AnonymousUser;
        var (user, perms) = await ResolvePermissionsAsync(actor, ct).ConfigureAwait(false);
        if (actor != AnonymousUser && (user is null || !user.IsActive)) return new();

        var userGroups = new List<string>();
        if (user?.Groups is { Count: > 0 } gs) userGroups.AddRange(gs);
        userGroups.Add(actor);

        var querySubpath = subpath.TrimStart('/');

        var policies = new List<string>();
        foreach (var p in perms)
        {
            if (!p.IsActive) continue;
            if (!p.Actions.Contains("query", StringComparer.Ordinal)) continue;

            foreach (var (permSpace, permSubpathList) in p.Subpaths)
            {
                var list = permSubpathList.Count > 0 ? permSubpathList : new List<string> { "/" };
                foreach (var rawSub in list)
                {
                    var permSubpath = rawSub.TrimStart('/');

                    var ownerShortname = user?.OwnerShortname;
                    if (!string.IsNullOrEmpty(ownerShortname))
                        permSubpath = permSubpath.Replace(CurrentUserOwnerMw, ownerShortname);
                    permSubpath = permSubpath.Replace(CurrentUserMw, actor);

                    var include =
                        permSpace == AllSpacesMw
                        || (permSpace == spaceName && (
                            permSubpath == AllSubpathsMw
                            || permSubpath == querySubpath
                            || (permSubpath.Length > 0
                                && querySubpath.StartsWith(permSubpath + "/", StringComparison.Ordinal))));
                    if (!include) continue;

                    var effectiveSpace = permSpace == AllSpacesMw ? spaceName : permSpace;
                    var effectiveSubpath = permSubpath == AllSubpathsMw ? querySubpath : permSubpath;

                    var resourceTypes = p.ResourceTypes.Count > 0 ? p.ResourceTypes : new() { "*" };
                    foreach (var rt in resourceTypes)
                    {
                        var permKey = $"{effectiveSpace}:{effectiveSubpath}:{rt}";
                        var hasIsActive = p.Conditions.Contains("is_active", StringComparer.Ordinal);
                        var hasOwn = p.Conditions.Contains("own", StringComparer.Ordinal);

                        if (hasIsActive && hasOwn)
                        {
                            foreach (var g in userGroups)
                                policies.Add($"{permKey}:true:{g}");
                        }
                        else if (hasIsActive) policies.Add($"{permKey}:true:*");
                        else if (hasOwn)
                        {
                            policies.Add($"{permKey}:true:{actor}");
                            policies.Add($"{permKey}:false:{actor}");
                        }
                        else policies.Add($"{permKey}:*");
                    }
                }
            }
        }
        return policies;
    }

    // ===================================================================
    // User + role + permission loading from Postgres
    // ===================================================================

    private async Task<(LoadedUser? User, List<DmartPermission> Perms)> ResolvePermissionsAsync(
        string actor, CancellationToken ct)
    {
        if (_cache.TryGetValue(actor, out var cached) && cached.Expiry > DateTime.UtcNow)
            return (cached.User, cached.Permissions);

        var user = await LoadUserForAuthzAsync(actor, ct).ConfigureAwait(false);
        var isAnonymous = actor == AnonymousUser;
        if (!isAnonymous && (user is null || !user.IsActive))
            return (user, new());

        var roleNames = new List<string>();
        if (user is not null)
        {
            roleNames.AddRange(user.Roles);
            if (!isAnonymous && !roleNames.Contains(ImplicitAuthenticatedRole, StringComparer.Ordinal))
                roleNames.Add(ImplicitAuthenticatedRole);
        }

        var roles = roleNames.Count == 0 ? new() : await LoadRolesAsync(roleNames, ct).ConfigureAwait(false);
        var permNames = roles.Where(r => r.IsActive).SelectMany(r => r.Permissions).Distinct().ToList();
        var perms = permNames.Count == 0 ? new() : await LoadPermissionsAsync(permNames, ct).ConfigureAwait(false);

        // Anonymous gets the "world" permission appended (when at least one
        // role resolved). Mirrors the dmart server's behavior.
        if (isAnonymous && roles.Count > 0)
        {
            var world = await LoadPermissionAsync(WorldPermission, ct).ConfigureAwait(false);
            if (world is not null && !perms.Any(p => p.Shortname == WorldPermission))
                perms.Add(world);
        }

        _cache[actor] = new CachedAccess(user, perms, DateTime.UtcNow + _cacheTtl);
        return (user, perms);
    }

    // Returns null for either "no row" OR "row exists but is_active=false".
    // Centralizing the active check here means callers don't have to remember
    // to re-check IsActive on every code path — an inactive user is
    // indistinguishable from a missing user as far as authorization is
    // concerned.
    private async Task<LoadedUser?> LoadUserForAuthzAsync(string shortname, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT shortname, is_active, roles, groups, owner_shortname FROM users WHERE shortname=$1",
            conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        var isActive = r.GetBoolean(1);
        if (!isActive) return null;
        return new LoadedUser
        {
            Shortname = r.GetString(0),
            IsActive = isActive,
            Roles = r.ReadJsonb<List<string>>(2, _json) ?? new(),
            Groups = r.ReadJsonb<List<string>>(3, _json) ?? new(),
            OwnerShortname = r.IsDBNull(4) ? null : r.GetString(4),
        };
    }

    private async Task<List<DmartRole>> LoadRolesAsync(List<string> names, CancellationToken ct)
    {
        var arr = names.ToArray();
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT shortname, is_active, permissions FROM roles WHERE shortname = ANY($1)",
            conn);
        cmd.Parameters.Add(new() { Value = arr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<DmartRole>(arr.Length);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DmartRole
            {
                Shortname = r.GetString(0),
                IsActive = r.GetBoolean(1),
                Permissions = r.ReadJsonb<List<string>>(2, _json) ?? new(),
            });
        }
        return result;
    }

    private async Task<List<DmartPermission>> LoadPermissionsAsync(List<string> names, CancellationToken ct)
    {
        var arr = names.ToArray();
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT shortname, is_active, subpaths, resource_types, actions, conditions,
                   restricted_fields, allowed_fields_values
              FROM permissions WHERE shortname = ANY($1)
            """, conn);
        cmd.Parameters.Add(new() { Value = arr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<DmartPermission>(arr.Length);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(HydratePermission(r));
        }
        return result;
    }

    private async Task<DmartPermission?> LoadPermissionAsync(string name, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT shortname, is_active, subpaths, resource_types, actions, conditions,
                   restricted_fields, allowed_fields_values
              FROM permissions WHERE shortname = $1
            """, conn);
        cmd.Parameters.Add(new() { Value = name });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? HydratePermission(r) : null;
    }

    private DmartPermission HydratePermission(NpgsqlDataReader r) => new()
    {
        Shortname        = r.GetString(0),
        IsActive         = r.GetBoolean(1),
        Subpaths         = r.ReadJsonb<Dictionary<string, List<string>>>(2, _json) ?? new(),
        ResourceTypes    = r.ReadJsonb<List<string>>(3, _json) ?? new(),
        Actions          = r.ReadJsonb<List<string>>(4, _json) ?? new(),
        Conditions       = r.ReadJsonb<List<string>>(5, _json) ?? new(),
        RestrictedFields = r.ReadJsonb<List<string>>(6, _json),
        AllowedFieldsValues = r.ReadJsonb<Dictionary<string, object>>(7, _json),
    };

    // ===================================================================
    // Walk + match helpers (verbatim port from PermissionService)
    // ===================================================================

    internal static List<string> BuildSubpathWalk(string fullSubpath)
    {
        var result = new List<string> { "/" };
        if (string.IsNullOrEmpty(fullSubpath) || fullSubpath == "/") return result;
        var parts = fullSubpath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var acc = "";
        foreach (var p in parts)
        {
            acc = string.IsNullOrEmpty(acc) ? p : $"{acc}/{p}";
            result.Add(acc);
        }
        return result;
    }

    internal static string ToGlobalForm(string subpath)
    {
        if (string.IsNullOrEmpty(subpath) || subpath == "/") return AllSubpathsMw;
        var parts = subpath.Split('/');
        if (parts.Length == 1) return AllSubpathsMw;
        parts[^2] = AllSubpathsMw;
        return string.Join('/', parts);
    }

    private static bool CheckAtCandidate(
        List<DmartPermission> perms,
        string spaceName,
        string subpathKey,
        string resourceType,
        string action,
        HashSet<string> achievedConditions,
        Dictionary<string, object>? recordAttributes,
        string? actor)
    {
        foreach (var p in perms)
        {
            if (!p.IsActive) continue;
            if (!p.Actions.Contains(action, StringComparer.Ordinal)) continue;
            if (p.ResourceTypes.Count > 0
                && !p.ResourceTypes.Contains(resourceType, StringComparer.Ordinal))
                continue;

            var matched = false;
            if (p.Subpaths.TryGetValue(spaceName, out var patterns)
                && MatchesAnyPattern(patterns, subpathKey, actor))
                matched = true;
            else if (p.Subpaths.TryGetValue(AllSpacesMw, out var globalPatterns)
                     && MatchesAnyPattern(globalPatterns, subpathKey, actor))
                matched = true;
            if (!matched) continue;

            if (!CheckConditions(p.Conditions, achievedConditions, action)) continue;
            if (!CheckRestrictions(p.RestrictedFields, p.AllowedFieldsValues, action, recordAttributes))
                continue;

            return true;
        }
        return false;
    }

    private static bool MatchesAnyPattern(List<string> patterns, string subpathKey, string? actor)
    {
        foreach (var raw in patterns)
        {
            if (raw == AllSubpathsMw) return true;
            var pattern = NormalizePermissionSubpath(raw);
            if (pattern == subpathKey) return true;
            if (actor is not null && pattern.Contains(CurrentUserMw, StringComparison.Ordinal))
            {
                var resolved = pattern.Replace(CurrentUserMw, actor, StringComparison.Ordinal);
                if (resolved == subpathKey) return true;
            }
        }
        return false;
    }

    private static string NormalizePermissionSubpath(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return "/";
        var s = pattern.Replace("//", "/");
        if (s.Length > 1 && s[0] == '/') s = s[1..];
        if (s.Length > 1 && s[^1] == '/') s = s[..^1];
        return s.Length == 0 ? "/" : s;
    }

    private static bool CheckConditions(List<string> required, HashSet<string> achieved, string action)
    {
        if (action == "create" || action == "query") return true;
        if (required.Count == 0) return true;
        foreach (var c in required)
            if (!achieved.Contains(c)) return false;
        return true;
    }

    private static bool CheckRestrictions(
        List<string>? restrictedFields,
        Dictionary<string, object>? allowedFieldsValues,
        string action,
        Dictionary<string, object>? attrs)
    {
        if (action != "create" && action != "update") return true;
        if (attrs is null || attrs.Count == 0) return true;

        var flat = new Dictionary<string, object?>(StringComparer.Ordinal);
        FlattenAttrs(attrs, "", flat);

        if (restrictedFields is { Count: > 0 })
        {
            foreach (var rf in restrictedFields)
            {
                foreach (var key in flat.Keys)
                {
                    if (key == rf || key.StartsWith(rf + ".", StringComparison.Ordinal))
                        return false;
                }
            }
        }

        if (allowedFieldsValues is { Count: > 0 })
        {
            foreach (var (field, allowedRaw) in allowedFieldsValues)
            {
                if (!flat.TryGetValue(field, out var actual)) continue;
                if (!IsValueAllowed(actual, allowedRaw)) return false;
            }
        }

        return true;
    }

    private static void FlattenAttrs(Dictionary<string, object> source, string prefix, Dictionary<string, object?> dest)
    {
        foreach (var (k, v) in source)
        {
            var key = string.IsNullOrEmpty(prefix) ? k : $"{prefix}.{k}";
            switch (v)
            {
                case null: dest[key] = null; break;
                case Dictionary<string, object> nested: FlattenAttrs(nested, key, dest); break;
                case JsonElement el when el.ValueKind == JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                        FlattenJsonElement(prop.Value, $"{key}.{prop.Name}", dest);
                    break;
                case JsonElement el: dest[key] = JsonElementToScalar(el); break;
                default: dest[key] = v; break;
            }
        }
    }

    private static void FlattenJsonElement(JsonElement el, string key, Dictionary<string, object?> dest)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
                FlattenJsonElement(prop.Value, $"{key}.{prop.Name}", dest);
        }
        else dest[key] = JsonElementToScalar(el);
    }

    private static object? JsonElementToScalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String  => el.GetString(),
        JsonValueKind.True    => true,
        JsonValueKind.False   => false,
        JsonValueKind.Null    => null,
        JsonValueKind.Number  => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.Array   => el.EnumerateArray().Select(JsonElementToScalar).ToList(),
        _                     => el.GetRawText(),
    };

    private static bool IsValueAllowed(object? actual, object? allowedRaw)
    {
        var allowedList = NormalizeList(allowedRaw);
        if (allowedList is null || allowedList.Count == 0) return true;

        var actualIsList = actual is List<object>
            || (actual is JsonElement aeProbe && aeProbe.ValueKind == JsonValueKind.Array);
        if (actualIsList)
        {
            var actualItems = NormalizeList(actual);
            if (actualItems is null) return false;

            if (allowedList[0] is List<object> or JsonElement { ValueKind: JsonValueKind.Array })
            {
                foreach (var sublistRaw in allowedList)
                {
                    var sublist = NormalizeList(sublistRaw);
                    if (sublist is null) continue;
                    if (actualItems.All(item => sublist.Any(allowed => ScalarEquals(item, allowed))))
                        return true;
                }
                return false;
            }
            return actualItems.All(item => allowedList.Any(allowed => ScalarEquals(item, allowed)));
        }

        return allowedList.Any(allowed => ScalarEquals(actual, allowed));
    }

    private static List<object?>? NormalizeList(object? raw) => raw switch
    {
        null                                                       => null,
        List<object> l                                             => l.Cast<object?>().ToList(),
        JsonElement el when el.ValueKind == JsonValueKind.Array    => el.EnumerateArray().Select(JsonElementToScalar).ToList(),
        _                                                          => new() { raw },
    };

    private static bool ScalarEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is JsonElement aj) a = JsonElementToScalar(aj);
        if (b is JsonElement bj) b = JsonElementToScalar(bj);
        if (a is null || b is null) return Equals(a, b);
        if (a is IConvertible && b is IConvertible)
        {
            try
            {
                if (a is string || b is string)
                    return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
                return Convert.ToDouble(a, System.Globalization.CultureInfo.InvariantCulture)
                    == Convert.ToDouble(b, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (InvalidCastException) { return a.Equals(b); }
            catch (FormatException) { return a.Equals(b); }
            catch (OverflowException) { return a.Equals(b); }
        }
        return a.Equals(b);
    }

    private static string EnumWire(Enum value) => value.ToString().ToLowerInvariant();

    // ===================================================================
    // Internal types
    // ===================================================================

    internal sealed class LoadedUser
    {
        public required string Shortname { get; init; }
        public bool IsActive { get; init; }
        public List<string> Roles { get; init; } = new();
        public List<string> Groups { get; init; } = new();
        public string? OwnerShortname { get; init; }
    }

    private sealed record CachedAccess(LoadedUser? User, List<DmartPermission> Permissions, DateTime Expiry);
}
