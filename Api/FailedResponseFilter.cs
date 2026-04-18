using Dmart.Models.Api;
using Dmart.Models.Json;

namespace Dmart.Api;

// Endpoint filter that maps Response objects with Status.Failed to the
// appropriate HTTP 4xx status code. Without this, ASP.NET minimal APIs
// serialize every Response as HTTP 200 regardless of the status field.
//
// Endpoints that already return IResult (e.g. Results.Json(..., statusCode: 401))
// pass through unchanged — the filter only transforms raw Response objects.
public sealed class FailedResponseFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var result = await next(context);

        if (result is Response { Status: Status.Failed } resp)
        {
            var httpStatus = MapErrorToHttpStatus(resp.Error?.Code);
            return Results.Json(resp, DmartJsonContext.Default.Response, statusCode: httpStatus);
        }

        return result;
    }

    internal static int MapErrorToHttpStatus(int? code) => code switch
    {
        // Auth / identity — HTTP 401
        InternalErrorCode.NOT_ALLOWED => 401,
        InternalErrorCode.NOT_AUTHENTICATED => 401,
        InternalErrorCode.INVALID_TOKEN => 401,
        InternalErrorCode.EXPIRED_TOKEN => 401,
        InternalErrorCode.SESSION => 401,
        InternalErrorCode.INVALID_USERNAME_AND_PASS => 401,
        InternalErrorCode.USER_ACCOUNT_LOCKED => 401,
        InternalErrorCode.USER_ISNT_VERIFIED => 401,

        // Not found — HTTP 404
        InternalErrorCode.SHORTNAME_DOES_NOT_EXIST => 404,
        InternalErrorCode.OBJECT_NOT_FOUND => 404,
        InternalErrorCode.DIR_NOT_FOUND => 404,
        InternalErrorCode.USERNAME_NOT_EXIST => 404,

        // Conflict / duplicate — HTTP 409
        InternalErrorCode.CONFLICT => 409,
        InternalErrorCode.SHORTNAME_ALREADY_EXIST => 409,
        InternalErrorCode.ALREADY_EXIST_SPACE_NAME => 409,
        InternalErrorCode.DATA_SHOULD_BE_UNIQUE => 409,

        // Forbidden — HTTP 403 (Python parity: /user/otp-request resend cooldown).
        InternalErrorCode.OTP_RESEND_BLOCKED => 403,

        // Locked — HTTP 423
        InternalErrorCode.LOCKED_ENTRY => 423,
        InternalErrorCode.LOCK_UNAVAILABLE => 423,

        // Everything else (bad_request, invalid data, OTP, QR, etc.) — HTTP 400
        _ => 400,
    };
}
