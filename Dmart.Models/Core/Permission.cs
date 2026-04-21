using System.Text.Json.Serialization;
using Dmart.Models.Enums;

namespace Dmart.Models.Core;

public sealed record Permission
{
    // ----- Unique base -----
    public required string Shortname { get; init; }
    public required string SpaceName { get; init; }
    public required string Subpath { get; init; }

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
    public ResourceType ResourceType { get; init; } = ResourceType.Permission;

    // ----- Permissions-specific -----
    public Dictionary<string, List<string>> Subpaths { get; init; } = new();
    public List<string> ResourceTypes { get; init; } = new();
    public List<string> Actions { get; init; } = new();
    public List<string> Conditions { get; init; } = new();
    public List<string>? RestrictedFields { get; init; }
    public Dictionary<string, object>? AllowedFieldsValues { get; init; }
    public string? FilterFieldsValues { get; init; }
    [JsonIgnore]
    public List<string> QueryPolicies { get; init; } = new();
}
