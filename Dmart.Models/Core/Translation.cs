namespace Dmart.Models.Core;

// Mirrors dmart/backend/models/core.py::Translation. Locale-keyed display string.
// Stored as JSONB in displayname/description columns.
public sealed record Translation(string? En = null, string? Ar = null, string? Ku = null);
