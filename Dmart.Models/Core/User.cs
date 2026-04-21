using System.Text.Json.Serialization;
using Dmart.Models.Enums;

namespace Dmart.Models.Core;

public sealed record User
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
    public Payload? Payload { get; init; }
    public string? LastChecksumHistory { get; init; }
    public ResourceType ResourceType { get; init; } = ResourceType.User;

    // ----- Users-specific -----
    [JsonIgnore]
    public string? Password { get; init; }    // hashed — never serialized to API responses
    public List<string> Roles { get; init; } = new();
    public List<string> Groups { get; init; } = new();
    public List<AclEntry>? Acl { get; init; }
    public List<Dictionary<string, object>>? Relationships { get; init; }
    public UserType Type { get; init; } = UserType.Web;
    public Language Language { get; init; } = Language.En;
    public string? Email { get; init; }
    public string? Msisdn { get; init; }
    public bool LockedToDevice { get; init; }
    public bool IsEmailVerified { get; init; }
    public bool IsMsisdnVerified { get; init; }
    public bool ForcePasswordChange { get; init; }
    public string? DeviceId { get; init; }
    public string? GoogleId { get; init; }
    public string? FacebookId { get; init; }
    public string? SocialAvatarUrl { get; init; }
    public int? AttemptCount { get; init; }
    public Dictionary<string, object>? LastLogin { get; init; }
    public string? Notes { get; init; }
    [JsonIgnore]
    public List<string> QueryPolicies { get; init; } = new();
}
