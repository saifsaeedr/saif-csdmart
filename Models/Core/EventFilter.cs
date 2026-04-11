namespace Dmart.Models.Core;

// Mirrors dmart/backend/models/core.py::EventFilter. The four fields all take
// string lists. The literal "__ALL__" acts as a wildcard — when it appears in
// any list, that dimension is unconstrained. Action strings are the string
// values of ActionType (create/update/delete/...).
public sealed record EventFilter
{
    public List<string> Subpaths { get; init; } = new();
    public List<string> ResourceTypes { get; init; } = new();
    public List<string> SchemaShortnames { get; init; } = new();
    public List<string> Actions { get; init; } = new();
}
