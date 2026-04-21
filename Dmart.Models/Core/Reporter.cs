namespace Dmart.Models.Core;

// Mirrors dmart/backend/models/core.py::Reporter — used by Tickets.
public sealed record Reporter
{
    public string? Type { get; init; }
    public string? Name { get; init; }
    public string? Channel { get; init; }
    public string? Distributor { get; init; }
    public string? Governorate { get; init; }
    public string? Msisdn { get; init; }
    public Dictionary<string, object>? ChannelAddress { get; init; }
}
