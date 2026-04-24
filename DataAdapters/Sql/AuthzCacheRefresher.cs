using System.Collections.Concurrent;
using Dmart.Models.Core;

namespace Dmart.DataAdapters.Sql;

// Owns the process-local in-memory cache of resolved (User, Permissions) tuples
// that PermissionService consults on every request. Centralizing the cache here
// means:
//
//   * UserRepository / AccessRepository call RefreshAsync on every write that
//     could affect access control, so the in-memory cache is invalidated
//     automatically without PermissionService having to subscribe to anything.
//
//   * PermissionService can stay a thin singleton — it just calls
//     refresher.GetCachedUserAccess / SetCachedUserAccess.
//
// Tests that need a clean slate call InvalidateAllInMemory() (or write any
// user/role/permission, which triggers the same path).
public sealed class AuthzCacheRefresher
{
    // The resolved access bundle for one user. Holds the User row (so callers can
    // check IsActive, Groups, etc.) plus the flattened list of Permission rows
    // reachable via user.Roles → role.Permissions → permission rows.
    public sealed record CachedUserAccess(User? User, List<Permission> Permissions);

    // Process-local cache. Singleton lifetime ensures every request sees the same
    // dictionary. ConcurrentDictionary is lock-free for reads on the hot path.
    private readonly ConcurrentDictionary<string, CachedUserAccess> _userAccess = new();

    public CachedUserAccess? GetCachedUserAccess(string shortname)
        => _userAccess.TryGetValue(shortname, out var v) ? v : null;

    public void SetCachedUserAccess(string shortname, CachedUserAccess value)
        => _userAccess[shortname] = value;

    // Clears the in-memory user-access cache. Called from RefreshAsync and from
    // AccessRepository.InvalidateAllCachesAsync.
    public void InvalidateAllInMemory() => _userAccess.Clear();

    public Task RefreshAsync(CancellationToken ct = default)
    {
        InvalidateAllInMemory();
        return Task.CompletedTask;
    }
}
