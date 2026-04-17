using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;

namespace Dmart.Api.User;

public static class OtpHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // All OTP endpoints share the "auth-by-ip" rate limit: they can trigger
        // SMS/email sends or verify codes — both vectors attackers exploit to
        // enumerate accounts or burn through OTP search space.
        g.MapPost("/otp-request", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            if (string.IsNullOrEmpty(dest)) return Response.Fail("bad_request", "destination required");
            var code = otp.Generate();
            await repo.StoreAsync(dest, code, DateTime.UtcNow.AddMinutes(10), ct);
            await otp.SendAsync(dest, code, ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        g.MapPost("/otp-request-login", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            var code = otp.Generate();
            await repo.StoreAsync($"login:{dest}", code, DateTime.UtcNow.AddMinutes(5), ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        g.MapPost("/password-reset-request", async (PasswordResetRequest req, OtpProvider otp, OtpRepository repo, CancellationToken ct) =>
        {
            var dest = req.Email ?? req.Msisdn ?? req.Shortname ?? "";
            var code = otp.Generate();
            await repo.StoreAsync($"reset:{dest}", code, DateTime.UtcNow.AddMinutes(15), ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Python's otp-confirm verifies the OTP and then marks the email/msisdn
        // as verified on the user row. We need the user context to do that.
        g.MapPost("/otp-confirm", async (ConfirmOTPRequest req, OtpRepository repo,
            UserRepository users, HttpContext http, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            var ok = await repo.VerifyAndConsumeAsync(dest, req.Code, ct);
            if (!ok) return Response.Fail("invalid_otp", "code mismatch or expired");

            // If the caller is authenticated, update their verified flags.
            var actor = http.User.Identity?.Name;
            if (actor is not null)
            {
                var user = await users.GetByShortnameAsync(actor, ct);
                if (user is not null)
                {
                    var updated = user with
                    {
                        IsEmailVerified = !string.IsNullOrEmpty(req.Email) || user.IsEmailVerified,
                        IsMsisdnVerified = !string.IsNullOrEmpty(req.Msisdn) || user.IsMsisdnVerified,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    await users.UpsertAsync(updated, ct);
                }
            }
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");
    }
}
