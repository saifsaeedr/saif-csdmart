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
    // Delete-only: when true, folder deletes cascade and user deletes do a full
    // owned-data cascade. The response reports the count of removed rows (`affected`)
    // plus a per-category `report` breakdown.
    public bool Force { get; init; } = false;
    // Delete-only: when true, nothing is removed — the delete is projected and the
    // response reports the count that WOULD be removed (ignoring `force`), with
    // `dry_run: true`. Wire field: `dry_run`.
    public bool DryRun { get; init; } = false;
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
