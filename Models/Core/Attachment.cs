using Dmart.Models.Enums;

namespace Dmart.Models.Core;

// Mirrors dmart's `Attachments` table — Metas + media bytes + body + state.
public sealed record Attachment
{
    // ----- Unique base -----
    public required string Shortname { get; init; }
    public required string SpaceName { get; init; }

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
    public required ResourceType ResourceType { get; init; }

    // ----- Attachments-specific -----
    public Locator? AuthorLocator { get; init; }
    public byte[]? Media { get; init; }
    public string? Body { get; init; }
    public string? State { get; init; }
}
