using Dmart.Models.Api;

namespace Dmart.Utils;

// Lightweight Result<T> for service-layer outcomes. Failure carries the exact
// wire triple: integer InternalErrorCode, human-readable message, and type
// string ("auth", "db", "request", "jwtauth", "qr", "internal", …).
//
// Matches Python's `api.Exception(..., error=api.Error(type=..., code=..., message=...))`
// so the HTTP response can emit the triple verbatim. There is no string-name
// shortcut — every caller supplies the integer constant and type explicitly.
public readonly record struct Result<T>(
    bool IsOk,
    T? Value,
    int ErrorCode,
    string? ErrorMessage,
    string? ErrorType)
{
    public static Result<T> Ok(T value) => new(true, value, 0, null, null);

    public static Result<T> Fail(int code, string message, string type) =>
        new(false, default, code, message, type);
}
