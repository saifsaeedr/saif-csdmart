using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;

namespace Dmart.Tests.Integration;

// Single provisioning path for the reserved "world" permission. permissions
// (shortname) is globally unique, so every test that grants anonymous access must
// UPDATE the one canonical "world" row, not INSERT a second one at a different
// subpath. Passing the captured priorWorld targets that row's own subpath
// (ON CONFLICT (shortname, space_name, subpath) then matches → UPDATE in place);
// a fresh DB with no world row falls back to the canonical "/permissions".
//
// Tests still capture priorWorld up front and restore/delete it on teardown — this
// only fixes the CREATE so it never forks a duplicate. All callers live in the
// AnonymousWorld collection, so they run serially.
internal static class WorldPermissionFixture
{
    public const string Shortname = "world";
    public const string CanonicalSubpath = "/permissions";

    public static Task UpsertAsync(
        AccessRepository access,
        Permission? priorWorld,
        Dictionary<string, List<string>> subpaths,
        List<string> actions,
        List<string>? resourceTypes = null,
        List<string>? conditions = null,
        bool isActive = true)
        => access.UpsertPermissionAsync(new Permission
        {
            Uuid = priorWorld?.Uuid ?? Guid.NewGuid().ToString(),
            Shortname = Shortname,
            SpaceName = "management",
            Subpath = priorWorld?.Subpath ?? CanonicalSubpath,
            OwnerShortname = "dmart",
            IsActive = isActive,
            Subpaths = subpaths,
            ResourceTypes = resourceTypes ?? new() { "content" },
            Actions = actions,
            Conditions = conditions ?? new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
}
