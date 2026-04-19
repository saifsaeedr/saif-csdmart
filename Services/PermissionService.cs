using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Services;

// Mirrors dmart Python's utils/access_control.py::AccessControl.check_access. Walks the
// user's roles → role.permissions, then for each candidate (subpath, magic-word) form
// looks for a Permission row that grants the requested action+resource_type and whose
// conditions/restrictions are satisfied. Also honors per-entry ACL grants on the target
// resource (the Acl field on Entry/User/Role/Permission/Space/Attachment).
//
// Implementation notes:
//
//   * The `super_admin` role no longer has a hard-coded short-circuit. AdminBootstrap
//     attaches a `super_manager` permission to it that uses dmart's __all_spaces__/
//     __all_subpaths__ magic words, so super_admin still grants everything — but it
//     does so through the same code path as any other role, which keeps behavior in
//     sync with dmart Python and makes benchmarks comparable.
//
//   * The hierarchical subpath walk iterates from "/" outward (e.g. for /a/b/c we check
//     "/", "a", "a/b", "a/b/c"). At each step we also try a "global form" that replaces
//     the second-to-last segment with __all_subpaths__ — this is dmart's convention
//     for "match any descendant under this branch" (see has_global_access in Python).
//
//   * Conditions ("own", "is_active") are evaluated against a ResourceContext that the
//     caller passes. If no context is provided, conditions cannot be satisfied and any
//     permission that requires them will be skipped — except for "create" and "query"
//     actions, which Python explicitly exempts from condition checks.
//
//   * Field-level restrictions (restricted_fields, allowed_fields_values) only apply
//     to "create" and "update" actions. The caller must pass the request attributes
//     dict for these checks to fire.
public sealed class PermissionService(UserRepository users, AccessRepository access, AuthzCacheRefresher cache)
{
    // dmart sentinel values from utils/settings.py.
    public const string AllSpacesMw   = "__all_spaces__";
    public const string AllSubpathsMw = "__all_subpaths__";
    // __current_user__ inside a permission subpath resolves to the caller's shortname
    // at check time. Used for per-user personal paths like
    // "people/__current_user__/protected" — see access_personal in the sample data.
    // Python: data_adapters/helpers.py::trans_magic_words.
    public const string CurrentUserMw = "__current_user__";

    // Compact bag of the resource being checked. We accept it as a record so callers
    // can synthesize one from an Entry, User, Role, etc. without coupling
    // PermissionService to those concrete types. Pass null when the resource hasn't
    // been (or can't be) loaded — e.g. on a query subpath probe.
    public sealed record ResourceContext(
        bool IsActive,
        string? OwnerShortname,
        string? OwnerGroupShortname,
        List<AclEntry>? Acl);

    public static ResourceContext FromEntry(Entry e) =>
        new(e.IsActive, e.OwnerShortname, e.OwnerGroupShortname, e.Acl);

    public static ResourceContext FromUser(User u) =>
        new(u.IsActive, u.OwnerShortname, u.OwnerGroupShortname, u.Acl);

    // Every authenticated user implicitly gains this role. Python: SQL adapter's
    // get_user_roles injects the 'logged_in' role for every non-anonymous user,
    // which is what grants access_personal/access_protected/etc. to ordinary
    // logins. Without this, roles like 'sub_dealer' with no explicit personal-space
    // grants would be locked out of their own /people/{shortname}/* subtree.
    private const string ImplicitAuthenticatedRole = "logged_in";
    // Python dmart treats unauthenticated callers as user_shortname="anonymous".
    // Public endpoints (/public/query, /public/entry) route through this path so
    // that __all_spaces__ / __all_subpaths__ magic words in the anonymous user's
    // roles (or the special "world" permission) resolve the same way they would
    // for any other user.
    private const string AnonymousUser = "anonymous";
    private const string WorldPermission = "world";

    // Resolves the user + flattened permission list, using the in-memory cache.
    // Shared by CanAsync and HasAnyAccessToSpaceAsync to avoid duplicating the
    // user → roles → permissions loading logic.
    //
    // For actor="anonymous" we additionally append the "world" permission (if
    // configured) — Python's generate_user_permissions() does this explicitly
    // so a deployment can expose a public read surface by creating a single
    // "world" permission with __all_spaces__:__all_subpaths__.
    private async Task<(User? User, List<Permission> Perms)> ResolvePermissionsAsync(
        string actorShortname, CancellationToken ct)
    {
        var cached = cache.GetCachedUserAccess(actorShortname);
        if (cached is not null)
            return (cached.User, cached.Permissions);

        var user = await users.GetByShortnameAsync(actorShortname, ct);
        var isAnonymous = actorShortname == AnonymousUser;
        // For a missing/inactive named user we short-circuit. For the anonymous
        // bucket we continue even when no "anonymous" row exists so the cache
        // entry records "no permissions" — avoids re-querying the users table
        // on every unauthenticated request.
        if (!isAnonymous && (user is null || !user.IsActive))
            return (user, new());

        var roleNames = new List<string>();
        if (user is not null)
        {
            roleNames.AddRange(user.Roles);
            if (!isAnonymous && !roleNames.Contains(ImplicitAuthenticatedRole, StringComparer.Ordinal))
                roleNames.Add(ImplicitAuthenticatedRole);
        }

        var roles = roleNames.Count == 0 ? new() : await access.GetRolesAsync(roleNames, ct);
        var permNames = roles.SelectMany(r => r.Permissions).Distinct().ToList();
        var perms = permNames.Count == 0 ? new() : await access.GetPermissionsAsync(permNames, ct);

        // Python parity: `generate_user_permissions` appends the "world" record
        // INSIDE the role-iteration loop (adapter.py:3283-3290), so an anonymous
        // caller with zero roles resolves to zero permissions — world is never
        // consulted. To match, we require roles.Count > 0. Admins must create
        // an "anonymous" user row with at least one role for world to apply.
        // Don't re-filter by world.IsActive here — the permission walk already
        // skips inactive permissions.
        if (isAnonymous && roles.Count > 0)
        {
            var world = await access.GetPermissionAsync(WorldPermission, ct);
            if (world is not null && !perms.Any(p => p.Shortname == WorldPermission))
                perms.Add(world);
        }

        cache.SetCachedUserAccess(actorShortname, new(user, perms));
        return (user, perms);
    }

    public async Task<bool> CanAsync(
        string? actorShortname,
        string action,
        Locator target,
        ResourceContext? resource = null,
        Dictionary<string, object>? recordAttributes = null,
        CancellationToken ct = default)
    {
        // Anonymous: resolve permissions the same way authenticated users do —
        // via the "anonymous" user + the "world" permission. Writes will fall
        // through naturally when no permission grants them; no need for an
        // action != "view" shortcut.
        actorShortname ??= AnonymousUser;

        var (user, perms) = await ResolvePermissionsAsync(actorShortname, ct);
        // Authenticated users must have a live row; anonymous is allowed to
        // resolve to no user (so "world" can still grant access even with no
        // anonymous user row in the DB).
        if (actorShortname != AnonymousUser && (user is null || !user.IsActive)) return false;

        // 1. Per-entry ACL: if the resource has an ACL entry naming this user with the
        //    requested action in their allowed_actions list, grant immediately.
        //    Python: AccessControl.check_access_control_list().
        if (resource?.Acl is { Count: > 0 } acls)
        {
            foreach (var entry in acls)
            {
                if (entry.UserShortname != actorShortname) continue;
                if (entry.Allowed is { Count: > 0 } allowed && allowed.Contains(action, StringComparer.Ordinal))
                    return true;
                // Explicit deny short-circuits — a denied action cannot fall through
                // to a role-based grant. (Python doesn't model deny; this is a C#
                // extension that the AclEntry.Denied field exposes.)
                if (entry.Denied is { Count: > 0 } denied && denied.Contains(action, StringComparer.Ordinal))
                    return false;
            }
        }

        // 2. No role-based permissions to consider — we already cached the empty list.
        if (perms.Count == 0) return false;

        // 3. Compute resource_achieved_conditions = {is_active?, own?}.
        //    Python: see check_access lines 46-49.
        var achieved = new HashSet<string>();
        if (resource is not null)
        {
            if (resource.IsActive) achieved.Add("is_active");
            if (!string.IsNullOrEmpty(resource.OwnerShortname) && resource.OwnerShortname == actorShortname)
                achieved.Add("own");
            // user is only null in the anonymous-with-no-db-row path; group
            // ownership can't apply there (no Groups to consult).
            else if (!string.IsNullOrEmpty(resource.OwnerGroupShortname) && user is not null &&
                     user.Groups.Contains(resource.OwnerGroupShortname))
                achieved.Add("own");
        }

        var rt = JsonbHelpers.EnumMember(target.Type);

        // Python: effective_space = entry_shortname when resource_type is space.
        // For a space named "evd", permissions are keyed under space_name "evd",
        // but the query comes with space_name "management" (the management space).
        var effectiveSpace = target.Type == ResourceType.Space
            && !string.IsNullOrEmpty(target.Shortname) && target.Shortname != "*"
            ? target.Shortname
            : target.SpaceName;

        // 4. Hierarchical subpath walk + global-form magic word at each level.
        //    Python appends the entry shortname for folder resource types so that
        //    a folder "users" at subpath "/" also checks the "users" permission key.
        var walkPath = target.Subpath;
        if (target.Type == ResourceType.Folder && !string.IsNullOrEmpty(target.Shortname) && target.Shortname != "*")
        {
            walkPath = walkPath == "/"
                ? target.Shortname
                : $"{walkPath.TrimEnd('/')}/{target.Shortname}";
        }
        foreach (var candidate in BuildSubpathWalk(walkPath))
        {
            if (CheckAtCandidate(perms, effectiveSpace, candidate, rt, action, achieved, recordAttributes, actorShortname))
                return true;
            var global = ToGlobalForm(candidate);
            if (global is not null && global != candidate &&
                CheckAtCandidate(perms, effectiveSpace, global, rt, action, achieved, recordAttributes, actorShortname))
                return true;
        }

        return false;
    }

    // Check if the user has ANY permission that references this space (by name or
    // via __all_spaces__). Used for the spaces listing — a user should see a space
    // if they have any grant within it, regardless of resource_type.
    public async Task<bool> HasAnyAccessToSpaceAsync(string? actorShortname, string spaceName, CancellationToken ct = default)
    {
        actorShortname ??= AnonymousUser;
        var (user, perms) = await ResolvePermissionsAsync(actorShortname, ct);
        if (actorShortname != AnonymousUser && (user is null || !user.IsActive)) return false;

        foreach (var p in perms)
        {
            if (!p.IsActive) continue;
            if (p.Subpaths.ContainsKey(spaceName) || p.Subpaths.ContainsKey(AllSpacesMw))
                return true;
        }
        return false;
    }

    // Batched version of HasAnyAccessToSpaceAsync for listing endpoints. Walks the
    // user's permissions once and intersects with the candidate space names,
    // instead of N calls that each re-iterate the permissions (the original N+1).
    public async Task<HashSet<string>> GetAccessibleSpacesAsync(
        string? actorShortname, IEnumerable<string> candidates, CancellationToken ct = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        actorShortname ??= AnonymousUser;
        var (user, perms) = await ResolvePermissionsAsync(actorShortname, ct);
        if (actorShortname != AnonymousUser && (user is null || !user.IsActive)) return result;

        // If any permission carries __all_spaces__, every candidate is visible.
        var hasGlobal = perms.Any(p => p.IsActive && p.Subpaths.ContainsKey(AllSpacesMw));
        if (hasGlobal)
        {
            foreach (var c in candidates) result.Add(c);
            return result;
        }

        // Otherwise collect the explicit space-name keys from active permissions
        // and intersect with the candidate list.
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in perms)
        {
            if (!p.IsActive) continue;
            foreach (var key in p.Subpaths.Keys) named.Add(key);
        }
        foreach (var c in candidates)
            if (named.Contains(c)) result.Add(c);
        return result;
    }

    // ----- back-compat convenience overloads (keep old call sites compiling) -----

    public Task<bool> CanReadAsync(string? actor, Locator l, CancellationToken ct = default)
        => CanAsync(actor, "view", l, null, null, ct);
    public Task<bool> CanCreateAsync(string? actor, Locator l, CancellationToken ct = default)
        => CanAsync(actor, "create", l, null, null, ct);
    public Task<bool> CanUpdateAsync(string? actor, Locator l, CancellationToken ct = default)
        => CanAsync(actor, "update", l, null, null, ct);
    public Task<bool> CanDeleteAsync(string? actor, Locator l, CancellationToken ct = default)
        => CanAsync(actor, "delete", l, null, null, ct);

    // ----- context-aware overloads for callers that have the entry in hand -----

    public Task<bool> CanReadAsync(string? actor, Locator l, ResourceContext? r, CancellationToken ct = default)
        => CanAsync(actor, "view", l, r, null, ct);
    public Task<bool> CanCreateAsync(string? actor, Locator l, Dictionary<string, object>? attrs, CancellationToken ct = default)
        => CanAsync(actor, "create", l, null, attrs, ct);
    public Task<bool> CanUpdateAsync(string? actor, Locator l, ResourceContext? r, Dictionary<string, object>? attrs, CancellationToken ct = default)
        => CanAsync(actor, "update", l, r, attrs, ct);
    public Task<bool> CanDeleteAsync(string? actor, Locator l, ResourceContext? r, CancellationToken ct = default)
        => CanAsync(actor, "delete", l, r, null, ct);

    public Task ReloadAsync(CancellationToken ct = default) => access.InvalidateAllCachesAsync(ct);

    // ============================================================================
    // Helpers
    // ============================================================================

    // Builds the list of subpath candidates the way Python iterates them. For "/" the
    // result is just ["/"]; for "/a/b/c" it's ["/", "a", "a/b", "a/b/c"]. The leading
    // slash only appears on the root entry — all subsequent entries match the
    // slash-stripped form that the DB stores in `permissions.subpaths` JSONB.
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

    // Replaces the second-to-last segment with __all_subpaths__ — Python's
    // has_global_access. For root or single-segment forms, the result is just the
    // magic word itself.
    internal static string ToGlobalForm(string subpath)
    {
        if (string.IsNullOrEmpty(subpath) || subpath == "/") return AllSubpathsMw;
        var parts = subpath.Split('/');
        if (parts.Length == 1) return AllSubpathsMw;
        // len ≥ 2: replace parts[len-2]
        parts[^2] = AllSubpathsMw;
        return string.Join('/', parts);
    }

    private static bool CheckAtCandidate(
        List<Permission> perms,
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
            if (p.ResourceTypes.Count > 0 && !p.ResourceTypes.Contains(resourceType, StringComparer.Ordinal))
                continue;

            // Subpath match: try the request's space first, then the __all_spaces__ bucket.
            var matched = false;
            if (p.Subpaths.TryGetValue(spaceName, out var patterns) && MatchesAnyPattern(patterns, subpathKey, actor))
                matched = true;
            else if (p.Subpaths.TryGetValue(AllSpacesMw, out var globalPatterns) && MatchesAnyPattern(globalPatterns, subpathKey, actor))
                matched = true;
            if (!matched) continue;

            if (!CheckConditions(p.Conditions, achievedConditions, action)) continue;
            if (!CheckRestrictions(p.RestrictedFields, p.AllowedFieldsValues, action, recordAttributes)) continue;

            return true;
        }
        return false;
    }

    // Patterns are mostly literal subpaths, but two magic words are honored:
    //   - __all_subpaths__ matches anything at this walk level (dmart's wildcard).
    //   - __current_user__ is replaced with the caller's shortname before comparison,
    //     so permissions like "people/__current_user__/protected" grant the caller
    //     access to their own personal subtree.
    // Each pattern is normalized via NormalizePermissionSubpath before equality —
    // Python does this implicitly via data_adapters.helpers.trans_magic_words,
    // which strips leading/trailing slashes and collapses "//" to "/". Without
    // this, permission rows stored as "/denominations" fail to match the walk
    // key "denominations" (which BuildSubpathWalk emits slash-free).
    internal static bool MatchesAnyPattern(List<string> patterns, string subpathKey, string? actor)
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

    // Python-parity subpath normalization (data_adapters/helpers.py::trans_magic_words
    // tail). Collapse "//" → "/", strip the leading "/" (unless the whole
    // pattern is "/"), strip the trailing "/". Applied to permission subpath
    // patterns so "/foo" and "foo" match the same walk candidate.
    internal static string NormalizePermissionSubpath(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return "/";
        var s = pattern.Replace("//", "/");
        if (s.Length > 1 && s[0] == '/') s = s[1..];
        if (s.Length > 1 && s[^1] == '/') s = s[..^1];
        return s.Length == 0 ? "/" : s;
    }

    // Python: check_access_conditions. Create + query actions are exempt from condition
    // checks (you can't ask "is the entry active" before it exists, and queries are
    // filtered post-hoc). For everything else, the permission's required conditions
    // must be a subset of those the resource achieves.
    internal static bool CheckConditions(List<string> required, HashSet<string> achieved, string action)
    {
        if (action == "create" || action == "query") return true;
        if (required.Count == 0) return true;
        foreach (var c in required)
            if (!achieved.Contains(c)) return false;
        return true;
    }

    // Python: check_access_restriction. Field restrictions only apply to create + update.
    // restricted_fields: any flattened-attribute key that equals or has a restricted
    // entry as a prefix (with `.` separator) blocks the operation.
    // allowed_fields_values: each entry constrains a specific flattened key to a set
    // of allowed values.
    internal static bool CheckRestrictions(
        List<string>? restrictedFields,
        Dictionary<string, object>? allowedFieldsValues,
        string action,
        Dictionary<string, object>? recordAttributes)
    {
        if (action != "create" && action != "update") return true;
        if (recordAttributes is null || recordAttributes.Count == 0) return true;

        var flat = new Dictionary<string, object?>(StringComparer.Ordinal);
        FlattenAttrs(recordAttributes, "", flat);

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

    // Recursively flattens nested dicts/JsonElements with `.` as the separator.
    // Mirrors Python's utils/helpers.flatten_dict for the keys we care about.
    internal static void FlattenAttrs(Dictionary<string, object> source, string prefix, Dictionary<string, object?> dest)
    {
        foreach (var (k, v) in source)
        {
            var key = string.IsNullOrEmpty(prefix) ? k : $"{prefix}.{k}";
            switch (v)
            {
                case null:
                    dest[key] = null;
                    break;
                case Dictionary<string, object> nested:
                    FlattenAttrs(nested, key, dest);
                    break;
                case JsonElement el when el.ValueKind == JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                        FlattenJsonElement(prop.Value, $"{key}.{prop.Name}", dest);
                    break;
                case JsonElement el:
                    dest[key] = JsonElementToScalar(el);
                    break;
                default:
                    dest[key] = v;
                    break;
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
        else
        {
            dest[key] = JsonElementToScalar(el);
        }
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

    // Compares an actual flattened value against the allowed-values list shape from the
    // permission. The shape is permissive: it can be a List<object> of scalars, a
    // List<List<object>> for "any of these allowed sublists", or a JsonElement Array.
    private static bool IsValueAllowed(object? actual, object? allowedRaw)
    {
        var allowedList = NormalizeList(allowedRaw);
        if (allowedList is null || allowedList.Count == 0) return true;

        // If the actual is a list, it's "all elements must be in some allowed sublist"
        // (matching Python's per-element check against List[List[allowed]]).
        var actualNormalized = NormalizeList(actual);
        var actualIsList = actual is List<object> ||
                           (actual is JsonElement aeProbe && aeProbe.ValueKind == JsonValueKind.Array);
        if (actualIsList && actualNormalized is not null)
        {
            var actualItems = actualNormalized;

            // If allowedList[0] is itself a list → "any of these allowed sublists"
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
            // Otherwise: every actual element must be in the flat allowed list.
            return actualItems.All(item => allowedList.Any(allowed => ScalarEquals(item, allowed)));
        }

        // Scalar actual: must equal one of the allowed entries.
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
        if (a is null || b is null) return a == b;
        // Cross-type numeric comparison: long vs double, int vs long, etc.
        if (a is IConvertible && b is IConvertible)
        {
            try
            {
                if (a is string || b is string) return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
                return Convert.ToDouble(a) == Convert.ToDouble(b);
            }
            catch
            {
                return a.Equals(b);
            }
        }
        return a.Equals(b);
    }
}
