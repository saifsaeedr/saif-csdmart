using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Microsoft.Extensions.Options;

namespace Dmart.Api.User;

public static class OtpHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // All OTP endpoints share the "auth-by-ip" rate limit: they can trigger
        // SMS/email sends or verify codes — both vectors attackers exploit to
        // enumerate accounts or burn through OTP search space.
        //
        // All endpoints also share a single TTL (settings.OtpTokenTtl, default
        // 300s) — Python uses one global value rather than per-endpoint
        // minutes.
        // Python parity: SendOTPRequest.check_fields() requires exactly one of
        // {shortname, msisdn, email}. Handler then looks up the user, enforces
        // a per-destination resend cooldown (allow_otp_resend_after), and
        // dispatches the OTP over SMS (msisdn) or email. Shortname-only
        // requests currently no-op on the send side — matches get_otp_key()
        // returning "" for shortname.
        g.MapPost("/otp-request", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo,
            UserRepository users, IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            var provided = (string.IsNullOrEmpty(req.Shortname) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Msisdn) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Email) ? 0 : 1);
            if (provided == 0)
                return Response.Fail(InternalErrorCode.EMAIL_OR_MSISDN_REQUIRED,
                    "One of these [email, msisdn, shortname] should be set!", "OTP");
            if (provided > 1)
                return Response.Fail(InternalErrorCode.INVALID_STANDALONE_DATA,
                    "Too many input has been passed", "OTP");

            Models.Core.User? user;
            string? dest = null;
            if (!string.IsNullOrEmpty(req.Shortname))
            {
                user = await users.GetByShortnameAsync(req.Shortname, ct);
            }
            else if (!string.IsNullOrEmpty(req.Msisdn))
            {
                user = await users.GetByMsisdnAsync(req.Msisdn, ct);
                dest = req.Msisdn;
            }
            else
            {
                var lower = req.Email!.ToLowerInvariant();
                user = await users.GetByEmailAsync(lower, ct);
                dest = lower;
            }

            var s = settings.Value;
            if (user is null && !s.IsRegistrable)
                return Response.Fail(InternalErrorCode.USERNAME_NOT_EXIST,
                    "No user found with the provided information", "request");

            if (dest is not null)
            {
                var since = await repo.GetCreatedSinceAsync(dest, ct);
                if (since is int elapsed && elapsed < s.AllowOtpResendAfter)
                    return Response.Fail(InternalErrorCode.OTP_RESEND_BLOCKED,
                        $"Resend OTP is allowed after {s.AllowOtpResendAfter - elapsed} seconds", "request");

                var code = otp.Generate();
                var expiresAt = DateTime.UtcNow.AddSeconds(s.OtpTokenTtl);
                await repo.StoreAsync(dest, code, expiresAt, ct);
                await otp.SendAsync(dest, code, ct);
            }

            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        g.MapPost("/otp-request-login", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            var code = otp.Generate();
            var expiresAt = DateTime.UtcNow.AddSeconds(settings.Value.OtpTokenTtl);
            await repo.StoreAsync($"login:{dest}", code, expiresAt, ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        g.MapPost("/password-reset-request", async (PasswordResetRequest req, OtpProvider otp, OtpRepository repo,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            var dest = req.Email ?? req.Msisdn ?? req.Shortname ?? "";
            var code = otp.Generate();
            var expiresAt = DateTime.UtcNow.AddSeconds(settings.Value.OtpTokenTtl);
            await repo.StoreAsync($"reset:{dest}", code, expiresAt, ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Python's otp-confirm verifies the OTP and then marks the email/msisdn
        // as verified on the user row. We need the user context to do that.
        g.MapPost("/otp-confirm", async (ConfirmOTPRequest req, OtpRepository repo,
            UserRepository users, HttpContext http, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            var ok = await repo.VerifyAndConsumeAsync(dest, req.Code, ct);
            if (!ok)
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", "auth");

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
