using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Api;

// Mirrors dmart's models/api.py::Response. Wire format:
//   { "status": "success" | "failed",
//     "error":  { "type": "...", "code": <int>, "message": "...", "info": [{}] } | null,
//     "records": [...] | null,
//     "attributes": {...} | null }
public sealed record Response
{
    public Status Status { get; init; } = Status.Success;
    public Error? Error { get; init; }
    public List<Record>? Records { get; init; }
    public Dictionary<string, object>? Attributes { get; init; }

    public static Response Ok(IEnumerable<Record>? records = null, Dictionary<string, object>? attributes = null)
        => new() { Status = Status.Success, Records = records?.ToList(), Attributes = attributes };

    // One canonical failure shape: (code, message, type). Mirrors Python's
    // api.Exception(error=api.Error(type=..., code=..., message=...)). Callers
    // MUST supply the InternalErrorCode integer and the Python-equivalent
    // type string ("auth", "jwtauth", "db", "request", "internal", "qr", …)
    // so clients see the same triple regardless of backend.
    public static Response Fail(int code, string message, string type,
        List<Dictionary<string, object>>? info = null)
        => new() { Status = Status.Failed, Error = new Error(type, code, message, info) };
}

// Mirrors dmart's models/enums.py::Status — StrEnum with values "success"/"failed".
[JsonConverter(typeof(StatusJsonConverter))]
public enum Status
{
    [EnumMember(Value = "success")] Success,
    [EnumMember(Value = "failed")]  Failed,
}

public sealed class StatusJsonConverter : EnumMemberConverterBase<Status> { }

// Mirrors dmart's models/api.py::Error — note `code` is an INT.
public sealed record Error(
    string Type,
    int Code,
    string Message,
    List<Dictionary<string, object>>? Info);
