using Dmart.Models.Core;

namespace Dmart.SqlAdapter.Permissions;

// Types consumed by PermissionEngine. Hydrated from `roles`, `permissions`,
// and `users` rows. Field shapes mirror dmart's SQL columns so a row maps 1:1.

public sealed record DmartRole
{
    public required string Shortname { get; init; }
    public bool IsActive { get; init; }
    public List<string> Permissions { get; init; } = new();
}

public sealed record DmartPermission
{
    public required string Shortname { get; init; }
    public bool IsActive { get; init; }
    // Keys are space names (or __all_spaces__); values are subpath patterns
    // (literal, __all_subpaths__, or __current_user__-style magic words).
    public Dictionary<string, List<string>> Subpaths { get; init; } = new();
    public List<string> ResourceTypes { get; init; } = new();
    public List<string> Actions { get; init; } = new();
    public List<string> Conditions { get; init; } = new();
    public List<string>? RestrictedFields { get; init; }
    public Dictionary<string, object>? AllowedFieldsValues { get; init; }
}

// Compact bag of the resource being checked. Built from an Entry/User/Space
// when the caller has the row in hand. Passing null means "resource not
// loaded yet" — Python treats this as "conditions can't be satisfied" for
// view/update/delete, but exempts create/query from the requirement.
public sealed record ResourceContext(
    bool IsActive,
    string? OwnerShortname,
    string? OwnerGroupShortname,
    List<AclEntry>? Acl)
{
    public static ResourceContext FromEntry(Entry e) =>
        new(e.IsActive, e.OwnerShortname, e.OwnerGroupShortname, e.Acl);

    public static ResourceContext FromUser(User u) =>
        new(u.IsActive, u.OwnerShortname, u.OwnerGroupShortname, u.Acl);

    public static ResourceContext FromSpace(Space s) =>
        new(s.IsActive, s.OwnerShortname, s.OwnerGroupShortname, s.Acl);
}

// Thrown when an actor lacks permission for the requested action. Carries
// the action + target so handlers can map it to a 403 response if exposing
// the adapter over HTTP.
public sealed class DmartPermissionDeniedException : Exception
{
    public string Actor { get; }
    public string Action { get; }
    public string SpaceName { get; }
    public string Subpath { get; }
    public string Shortname { get; }
    public string ResourceType { get; }

    public DmartPermissionDeniedException(string actor, string action,
        string spaceName, string subpath, string shortname, string resourceType)
        : base($"Permission denied for actor '{actor}': {action} on " +
               $"{spaceName}{subpath}/{shortname} ({resourceType})")
    {
        Actor = actor;
        Action = action;
        SpaceName = spaceName;
        Subpath = subpath;
        Shortname = shortname;
        ResourceType = resourceType;
    }
}
