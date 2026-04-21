using System.Diagnostics.CodeAnalysis;
using Dmart.Models.Enums;

namespace Dmart.Models.Core;

// Mirrors dmart/backend/models/core.py::Locator. Pinpoints an entry by space + subpath
// + shortname (+ resource type for routing).
//
// dmart's database stores subpaths with a LEADING SLASH (e.g. "/api"), even though
// the wire format on Record.subpath strips slashes (per dmart Python's Record.__init__).
// We normalize Locator.Subpath to the leading-slash form so the storage layer is
// consistent regardless of whether the caller came from the wire or from internal code.
public sealed record Locator
{
    public required ResourceType Type { get; init; }
    public required string SpaceName { get; init; }
    public required string Shortname { get; init; }

    private readonly string _subpath = "/";
    public required string Subpath
    {
        get => _subpath;
        init => _subpath = NormalizeSubpath(value);
    }

    public string? Uuid { get; init; }
    public string? Domain { get; init; }
    public string? SchemaShortname { get; init; }
    public Translation? Displayname { get; init; }
    public Translation? Description { get; init; }
    public List<string>? Tags { get; init; }

    [SetsRequiredMembers]
    public Locator(ResourceType type, string spaceName, string subpath, string shortname,
        string? uuid = null, string? domain = null, string? schemaShortname = null,
        Translation? displayname = null, Translation? description = null, List<string>? tags = null)
    {
        Type = type;
        SpaceName = spaceName;
        Subpath = subpath;
        Shortname = shortname;
        Uuid = uuid;
        Domain = domain;
        SchemaShortname = schemaShortname;
        Displayname = displayname;
        Description = description;
        Tags = tags;
    }

    public static string NormalizeSubpath(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "/") return "/";
        return "/" + s.Trim('/');
    }
}
