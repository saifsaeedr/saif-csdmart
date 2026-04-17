using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.User;

public static class RegistrationHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/create", async (HttpRequest req, UserService svc, CancellationToken ct) =>
        {
            var body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            if (body is null) return Response.Fail("bad_request", "missing body");
            var shortname = body.TryGetValue("shortname", out var sn) ? sn?.ToString() ?? "" : "";
            var email = body.TryGetValue("email", out var e) ? e?.ToString() : null;
            var msisdn = body.TryGetValue("msisdn", out var m) ? m?.ToString() : null;
            var password = body.TryGetValue("password", out var p) ? p?.ToString() : null;
            var language = body.TryGetValue("language", out var l) ? l?.ToString() : null;
            // Optional OTP fields — enforced only when IsOtpForCreateRequired=true.
            // The caller obtains these via POST /user/otp-request before registering.
            var emailOtp = body.TryGetValue("email_otp", out var eo) ? eo?.ToString() : null;
            var msisdnOtp = body.TryGetValue("msisdn_otp", out var mo) ? mo?.ToString() : null;
            var result = await svc.CreateAsync(shortname, email, msisdn, password, language, emailOtp, msisdnOtp, ct);
            if (!result.IsOk)
                return Response.Fail(result.ErrorCode!, result.ErrorMessage!);

            var (user, invitations) = result.Value;
            var attrs = new Dictionary<string, object>
            {
                ["uuid"] = user.Uuid,
                ["shortname"] = user.Shortname,
            };
            // Invitation JWTs for unverified channels — included on the
            // creation response so callers have something to hand back to
            // the new user while SMTP/SMS delivery is not yet wired up.
            if (invitations.Count > 0)
                attrs["invitations"] = invitations;
            return Response.Ok(attributes: attrs);
        });
    }
}
