using Dmart.Models.Api;
using Dmart.Services;

namespace Dmart.Api.Qr;

public static class ValidateHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/validate", async (string payload, QrService qr, CancellationToken ct)
            => await qr.ValidateAsync(payload, ct)
                ? Response.Ok()
                : Response.Fail(InternalErrorCode.QR_INVALID, "qr payload could not be validated", ErrorTypes.Qr));
}
