using System.Text.Json.Serialization;
using Dmart.Models.Enums;

namespace Dmart.Models.Core;

// Mirrors dmart's `Entries` SQLModel table — Metas inherited columns flattened
// + Entries-specific fields. Field names map directly to dmart's Python.
public sealed record Entry
{
    // ----- Unique base -----
    public required string Shortname { get; init; }
    public required string SpaceName { get; init; }

    // dmart's DB stores subpaths with a leading slash. Locator.NormalizeSubpath
    // ensures that whatever form the caller passes (stripped, leading-slash, /) ends
    // up consistent at the storage boundary.
    private readonly string _subpath = "/";
    public required string Subpath
    {
        get => _subpath;
        init => _subpath = Locator.NormalizeSubpath(value);
    }

    // ----- Metas base -----
    public required string Uuid { get; init; }
    public bool IsActive { get; init; }
    public string? Slug { get; init; }
    public Translation? Displayname { get; init; }
    public Translation? Description { get; init; }
    public List<string> Tags { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public required string OwnerShortname { get; init; }
    public string? OwnerGroupShortname { get; init; }
    public List<AclEntry>? Acl { get; init; }
    public Payload? Payload { get; init; }
    public List<Dictionary<string, object>>? Relationships { get; init; }
    public string? LastChecksumHistory { get; init; }
    public required ResourceType ResourceType { get; init; }   // stored as text in DB; converted at repo boundary

    // ----- Entries-specific (ticket fields) -----
    public string? State { get; init; }
    public bool? IsOpen { get; init; }
    public Reporter? Reporter { get; init; }
    public string? WorkflowShortname { get; init; }
    public Dictionary<string, string>? Collaborators { get; init; }
    public string? ResolutionReason { get; init; }
    [JsonIgnore]
    public List<string>? QueryPolicies { get; init; }
}
