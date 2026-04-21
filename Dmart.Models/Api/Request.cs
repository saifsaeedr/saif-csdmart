using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Models.Api;

// Mirrors dmart's models/api.py::Request — the unified CRUD envelope.
public sealed record Request
{
    public required RequestType RequestType { get; init; }
    public required string SpaceName { get; init; }
    public required List<Record> Records { get; init; }
    public Dictionary<string, object>? Attributes { get; init; }
}

// Mirrors dmart's models/core.py::Record. dmart's __init__ strips leading/trailing
// slashes from subpath unless it's literally "/" — we replicate that here so any
// Record produced or consumed by C# matches dmart's wire form byte-for-byte.
public sealed record Record
{
    private readonly string _subpath = "/";

    public required ResourceType ResourceType { get; init; }
    public required string Shortname { get; init; }

    public required string Subpath
    {
        get => _subpath;
        init => _subpath = NormalizeSubpath(value);
    }

    public string? Uuid { get; init; }
    public Dictionary<string, object>? Attributes { get; init; }
    public Dictionary<string, List<Record>>? Attachments { get; init; }
    public bool RetrieveLockStatus { get; init; }

    private static string NormalizeSubpath(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "/") return "/";
        return s.Trim('/');
    }
}
