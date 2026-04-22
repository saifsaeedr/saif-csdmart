using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Utils;

// Port of Python dmart's backend/utils/query_policies_helper.py::generate_query_policies.
// Produces the per-row LIKE-matchable patterns stored on entries.query_policies
// (TEXT[]). Every authenticated query runs AppendAclFilter which matches the
// caller's per-user policy list (built by PermissionService.BuildUserQueryPoliciesAsync)
// against these row patterns — so entries with an empty query_policies column
// are invisible to everyone except the owner and explicit ACL grants.
//
// Patterns emitted for space=X, subpath=/a/b, resource_type=content,
// is_active=true, owner=alice, owner_group=null:
//
//   X::content:true:alice
//   X::content:true
//   X:__all_subpaths__:content:true
//   X:a:content:true:alice
//   X:a:content:true
//   X:a/__all_subpaths__:content:true
//   X:a/b:content:true:alice
//   X:a/b:content:true
//
// The patterns walk the subpath tree from "/" outward, emitting at each level:
//   - owner-scoped literal
//   - owner-unscoped literal (or owner_group-scoped when a group is set)
//   - a "__all_subpaths__ at level N" global form (skipped at root)
public static class QueryPolicies
{
    public static List<string> Generate(Entry e) => Generate(
        spaceName: e.SpaceName,
        subpath: e.Subpath,
        resourceType: JsonbHelpers.EnumMember(e.ResourceType),
        isActive: e.IsActive,
        ownerShortname: e.OwnerShortname,
        ownerGroupShortname: e.OwnerGroupShortname,
        entryShortname: e.ResourceType == ResourceType.Folder ? e.Shortname : null);

    public static List<string> Generate(Attachment a) => Generate(
        spaceName: a.SpaceName,
        subpath: a.Subpath,
        resourceType: JsonbHelpers.EnumMember(a.ResourceType),
        isActive: a.IsActive,
        ownerShortname: a.OwnerShortname,
        ownerGroupShortname: a.OwnerGroupShortname,
        entryShortname: null);

    public static List<string> Generate(
        string spaceName,
        string subpath,
        string resourceType,
        bool isActive,
        string ownerShortname,
        string? ownerGroupShortname,
        string? entryShortname)
    {
        var parts = new List<string> { "/" };
        parts.AddRange(subpath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        // For folders, also emit patterns that include the folder's own
        // shortname as a subpath segment — mirrors Python's
        // `if resource_type == folder and entry_shortname: subpath_parts.append(entry_shortname)`.
        if (entryShortname is not null) parts.Add(entryShortname);

        var isActiveLiteral = isActive ? "true" : "false";
        var policies = new List<string>();
        var fullSubpath = "";
        foreach (var part in parts)
        {
            fullSubpath += part;
            var stripped = fullSubpath.Trim('/');

            // Literal + owner.
            policies.Add($"{spaceName}:{stripped}:{resourceType}:{isActiveLiteral}:{ownerShortname}");
            // Owner-unscoped (or owner_group-scoped when a group is set).
            policies.Add(ownerGroupShortname is null
                ? $"{spaceName}:{stripped}:{resourceType}:{isActiveLiteral}"
                : $"{spaceName}:{stripped}:{resourceType}:{isActiveLiteral}:{ownerGroupShortname}");

            // Global form — replace a middle segment with __all_subpaths__ so
            // permissions that match the subtree (without naming the specific
            // leaf) still grant. Only meaningful when the full_subpath has
            // more than one segment.
            var segs = fullSubpath.Split('/');
            if (segs.Length > 1)
            {
                var head = string.Join('/', segs.Take(1));
                var magicPath = $"{head}/{PermissionService.AllSubpathsMw}";
                if (segs.Length > 2)
                    magicPath += "/" + string.Join('/', segs.Skip(2));
                policies.Add($"{spaceName}:{magicPath.Trim('/')}:{resourceType}:{isActiveLiteral}");
            }

            fullSubpath = fullSubpath == "/" ? "" : fullSubpath + "/";
        }
        return policies;
    }
}
