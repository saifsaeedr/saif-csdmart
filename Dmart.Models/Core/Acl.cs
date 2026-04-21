namespace Dmart.Models.Core;

// dmart's ACL entries are stored as JSONB list-of-dicts on Metas. We model the entry
// shape so it round-trips through DmartJsonContext cleanly.
public sealed record AclEntry
{
    public string? UserShortname { get; init; }
    public List<string>? Allowed { get; init; }
    public List<string>? Denied { get; init; }
}
