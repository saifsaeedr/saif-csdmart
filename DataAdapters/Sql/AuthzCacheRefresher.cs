using System.Collections.Concurrent;
using Dmart.Models.Core;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Refreshes the materialized views dmart's permission resolver depends on:
//   * mv_user_roles      — derived from users.roles JSONB
//   * mv_role_permissions — derived from roles.permissions JSONB
//
// Both views have UNIQUE indexes so we can refresh them CONCURRENTLY (the dmart
// Python project does the same). The cache table user_permissions_cache is also
// invalidated whenever a role/permission changes structure.
//
// In addition to refreshing the DB-side materialized views, this class owns the
// process-local in-memory cache of resolved (User, Permissions) tuples that
// PermissionService consults on every request. Centralizing the cache here means:
//
//   * UserRepository / AccessRepository already call RefreshAsync on every write
//     that could affect access control, so the in-memory cache is invalidated
//     automatically without PermissionService having to subscribe to anything.
//
//   * PermissionService can stay a thin singleton — it just calls
//     refresher.GetCachedUserAccess / SetCachedUserAccess.
//
// Tests that need a clean slate call InvalidateAllInMemory() (or write any user/
// role/permission, which triggers the same path).
public sealed class AuthzCacheRefresher(Db db, ILogger<AuthzCacheRefresher> log)
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

    // Clears the in-memory user-access cache. Called from RefreshAsync (which
    // fires on every user/role/permission write that goes through the C# port)
    // and from AccessRepository.InvalidateAllCachesAsync.
    public void InvalidateAllInMemory() => _userAccess.Clear();

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Clear the in-memory cache FIRST so even if the MV refresh below fails
        // we don't keep serving stale permission decisions.
        InvalidateAllInMemory();

        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using (var c = new NpgsqlCommand(
                "REFRESH MATERIALIZED VIEW CONCURRENTLY mv_user_roles", conn))
                await c.ExecuteNonQueryAsync(ct);
            await using (var c = new NpgsqlCommand(
                "REFRESH MATERIALIZED VIEW CONCURRENTLY mv_role_permissions", conn))
                await c.ExecuteNonQueryAsync(ct);
            await using (var c = new NpgsqlCommand(
                "UPDATE authz_mv_meta SET refreshed_at = NOW() WHERE id = 1", conn))
                await c.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // Non-fatal: the next refresh will catch up. Log and continue so a
            // transient DB hiccup doesn't fail the surrounding write.
            log.LogWarning(ex, "authz mv refresh failed");
        }
    }
}
