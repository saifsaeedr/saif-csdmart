using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Services;
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
            UserRepository users, UserService userService, HttpContext http,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
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
            // OTP-request gate:
            //   * A JWT-bearing caller may always request an OTP (e.g. a
            //     logged-in user verifying/changing a contact) — UNLESS that
            //     account is locked, in which case issuance is refused with the
            //     same posture as /user/login.
            //   * An anonymous caller (no JWT) is gated by is_registrable: when
            //     self-registration is disabled there's no self-service reason
            //     to mint an OTP, regardless of whether the supplied identifier
            //     maps to an existing user (USERNAME_NOT_EXIST keeps that
            //     response identical to the old not-found branch — no oracle).
            var actor = http.Actor();
            if (actor is not null)
            {
                var jwtUser = await users.GetByShortnameAsync(actor, ct);
                if (jwtUser is not null && await userService.IsLockedAsync(jwtUser, ct))
                    return Response.Fail(InternalErrorCode.USER_ACCOUNT_LOCKED,
                        "Account has been locked.", ErrorTypes.Auth);
            }
            else if (!s.IsRegistrable)
            {
                return Response.Fail(InternalErrorCode.USERNAME_NOT_EXIST,
                    "No user found with the provided information", ErrorTypes.Request);
            }

            if (dest is not null)
            {
                var since = await repo.GetCreatedSinceAsync(dest, ct);
                if (since is int elapsed && elapsed < s.AllowOtpResendAfter)
                    return Response.Fail(InternalErrorCode.OTP_RESEND_BLOCKED,
                        $"Resend OTP is allowed after {s.AllowOtpResendAfter - elapsed} seconds", ErrorTypes.Request);

                var code = otp.Generate(dest);
                var expiresAt = TimeUtils.Now().AddSeconds(s.OtpTokenTtl);
                await repo.StoreAsync(dest, code, expiresAt, ct);
                // Use the registered user's language when known; default to
                // English for the registrable-anonymous path (no user yet).
                await otp.SendAsync(dest, code, user?.Language ?? Models.Enums.Language.En, ct);
            }

            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Python parity: accepts shortname/msisdn/email (exactly one), looks up
        // the user, and sends an OTP for login. Key-scheme is the same as
        // /otp-request so /user/login's OTP verification finds the code at the
        // same destination key (Python: both endpoints call send_otp which
        // writes to `users:otp:otps/{msisdn}` — no `login:` prefix).
        //
        // Anti-enumeration: when the user isn't found, Python still returns
        // {status: success} so callers can't probe which identifiers exist.
        g.MapPost("/otp-request-login", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo,
            UserRepository users, IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            var provided = (string.IsNullOrEmpty(req.Shortname) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Msisdn) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Email) ? 0 : 1);
            if (provided != 1)
                return Response.Fail(InternalErrorCode.OTP_ISSUE,
                    "one of msisdn, email or shortname must be provided", "auth");

            Models.Core.User? user;
            string? dest;
            if (!string.IsNullOrEmpty(req.Shortname))
            {
                user = await users.GetByShortnameAsync(req.Shortname, ct);
                // Shortname path: OTP is sent to the user's msisdn (Python parity).
                dest = user?.Msisdn;
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

            // Anti-enumeration: missing user, or shortname lookup with no
            // msisdn to SMS, both return silent success.
            if (user is null || !user.IsActive || string.IsNullOrEmpty(dest))
                return Response.Ok();

            var s = settings.Value;
            var code = otp.Generate(dest);
            var expiresAt = TimeUtils.Now().AddSeconds(s.OtpTokenTtl);
            await repo.StoreAsync(dest, code, expiresAt, ct);
            await otp.SendAsync(dest, code, user.Language, ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        g.MapPost("/password-reset-request", async (PasswordResetRequest req,
            UserRepository users, OtpProvider otp, OtpRepository repo,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            Models.Core.User? user = null;
            if (!string.IsNullOrEmpty(req.Shortname))
                user = await users.GetByShortnameAsync(req.Shortname, ct);
            else if (!string.IsNullOrEmpty(req.Msisdn))
                user = await users.GetByMsisdnAsync(req.Msisdn, ct);
            else if (!string.IsNullOrEmpty(req.Email))
                user = await users.GetByEmailAsync(req.Email, ct);

            // Anti-enumeration: response is identical whether the user exists
            // or not. All silent-no-op branches below also fall through to Ok().
            if (user is null) return Response.Ok();

            // Pick the destination using these routing rules:
            //   - Email-direct path: only when the request was email-only AND
            //     the supplied email matches the user record (case-insensitive
            //     equality guards against a mismatched email leaking through).
            //   - Msisdn / shortname path: prefer the user's msisdn; the msisdn
            //     equality check blocks an attacker probing whether a known
            //     shortname belongs to a specific msisdn.
            //   - Csdmart-only fallback: shortname-only request + user has no
            //     msisdn → fall back to email so the reset still reaches them.
            string? dest = null;
            var emailDirect = string.IsNullOrEmpty(req.Shortname)
                && string.IsNullOrEmpty(req.Msisdn)
                && !string.IsNullOrEmpty(req.Email);
            if (emailDirect)
            {
                if (!string.IsNullOrEmpty(user.Email)
                    && string.Equals(user.Email, req.Email, StringComparison.OrdinalIgnoreCase))
                    dest = user.Email;
            }
            else if (!string.IsNullOrEmpty(user.Msisdn)
                     && (string.IsNullOrEmpty(req.Msisdn)
                         || string.Equals(user.Msisdn, req.Msisdn, StringComparison.Ordinal)))
            {
                dest = user.Msisdn;
            }
            else if (!string.IsNullOrEmpty(req.Shortname)
                     && string.IsNullOrEmpty(req.Msisdn)
                     && !string.IsNullOrEmpty(user.Email))
            {
                dest = user.Email;
            }

            if (string.IsNullOrEmpty(dest)) return Response.Ok();

            // Reset OTPs live under a dedicated key (pwd-reset:{dest}) so the
            // login OTP path (which reads bare {dest}) can't consume them and
            // turn a password-reset code into a login credential.
            var s = settings.Value;
            var key = ResetOtpKey(dest);

            // Resend cooldown — scoped to the reset key, independent of the
            // generic /otp-request cooldown. Anti-enumeration: return Ok
            // silently when the cooldown is in effect so paired requests can't
            // distinguish "known user, just-issued OTP" from "unknown user"
            // (both observable as 200 Ok with no body). Trade-off: a
            // legitimate user who triple-taps "Resend" gets no feedback that
            // the second/third call was a no-op.
            var since = await repo.GetCreatedSinceAsync(key, ct);
            if (since is int elapsed && elapsed < s.AllowPasswordResetResendAfter)
                return Response.Ok();

            // OTP delivery: OtpProvider.SendAsync renders the body from the
            // `otp_message` language template, so the reset message uses the
            // same wording as /otp-request.
            var code = otp.Generate(dest);
            var expiresAt = TimeUtils.Now().AddSeconds(s.OtpTokenTtl);
            await repo.StoreAsync(key, code, expiresAt, ct);
            await otp.SendAsync(dest, code, user.Language, ct);

            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Completes the reset flow started by /password-reset-request:
        // verifies the OTP at the reset-scoped key, then writes the new
        // password hash on the user row. Typed identifier fields (same shape
        // as PasswordResetRequest) so the two halves resolve to the same user
        // — and the same `pwd-reset:{dest}` key — without heuristic shape
        // detection that could mis-route a numeric shortname.
        //
        // Uniform OTP_INVALID response for {unknown user, no dest, mismatch,
        // expired} so the endpoint doesn't leak which leg failed. Wrong OTPs
        // count against the same failed-attempt counter /user/login uses, so
        // an attacker can't brute-force the 6-digit code within its TTL
        // without tripping the account lockout.
        g.MapPost("/password-reset-confirm", async (PasswordResetConfirm req,
            UserRepository users, OtpRepository repo, PasswordHasher hasher,
            UserService userService, IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            // Exactly one of {Shortname, Email, Msisdn} — mirrors the shape
            // rules /otp-request and /password-reset-request use.
            var provided = (string.IsNullOrEmpty(req.Shortname) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Msisdn) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Email) ? 0 : 1);
            if (provided != 1 || string.IsNullOrWhiteSpace(req.Otp)
                || string.IsNullOrWhiteSpace(req.Password))
                return Response.Fail(InternalErrorCode.MISSING_DATA,
                    "exactly one of [shortname, email, msisdn] plus otp and password are required",
                    ErrorTypes.Request);

            // Resolve user via the typed identifier the caller supplied.
            Models.Core.User? user;
            if (!string.IsNullOrEmpty(req.Shortname))
                user = await users.GetByShortnameAsync(req.Shortname, ct);
            else if (!string.IsNullOrEmpty(req.Msisdn))
                user = await users.GetByMsisdnAsync(req.Msisdn, ct);
            else
                user = await users.GetByEmailAsync(req.Email!.ToLowerInvariant(), ct);

            if (user is null)
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            // Cheap-fails-first: validate password rules before the OTP probe.
            // VerifyAndConsumeAsync only deletes on success, so the OTP isn't
            // burned by this branch — but rejecting early avoids hashing work
            // and keeps the failure-mode predictable.
            if (!PasswordRules.IsValid(req.Password))
                return Response.Fail(InternalErrorCode.INVALID_PASSWORD_RULES,
                    "password does not meet complexity rules", ErrorTypes.Request);

            // Account lockout pre-check — match /user/login's posture so a
            // locked account can't be unlocked via the reset path either.
            if (!user.IsActive)
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            // Determine the dest /password-reset-request would have used so
            // we hit the same `pwd-reset:{dest}` key:
            //   email-direct + match → user.Email
            //   msisdn-direct or shortname-with-msisdn → user.Msisdn
            //   shortname-only no msisdn → user.Email
            string? dest = null;
            if (!string.IsNullOrEmpty(req.Email))
            {
                if (!string.IsNullOrEmpty(user.Email)
                    && string.Equals(user.Email, req.Email, StringComparison.OrdinalIgnoreCase))
                    dest = user.Email;
            }
            else if (!string.IsNullOrEmpty(user.Msisdn))
            {
                dest = user.Msisdn;
            }
            else if (!string.IsNullOrEmpty(req.Shortname)
                     && !string.IsNullOrEmpty(user.Email))
            {
                dest = user.Email;
            }

            if (string.IsNullOrEmpty(dest))
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            var ok = await repo.VerifyAndConsumeAsync(
                ResetOtpKey(dest), req.Otp, settings.Value.MaxOtpVerifyAttempts, ct);
            if (!ok)
            {
                // Wrong OTP counts against the failed-attempt counter — same
                // guarantee /user/login OTP path enforces. Without this, the
                // 6-digit code (10^6 keyspace, 300s TTL) is brute-forceable
                // by a distributed caller inside its own validity window.
                var locked = await userService.RecordFailedAttemptAsync(user, ct);
                return locked
                    ? Response.Fail(InternalErrorCode.USER_ACCOUNT_LOCKED,
                        "Account has been locked due to too many failed login attempts.",
                        ErrorTypes.Auth)
                    : Response.Fail(InternalErrorCode.OTP_INVALID,
                        "code mismatch or expired", ErrorTypes.Auth);
            }

            var updated = user with
            {
                Password = hasher.Hash(req.Password),
                ForcePasswordChange = false,
                UpdatedAt = TimeUtils.Now(),
            };
            await users.UpsertAsync(updated, ct);
            // Successful reset clears the failed-attempt counter — symmetric
            // with the password-login success path (ProcessLoginAsync).
            await users.ResetAttemptsAsync(user.Shortname, ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Python's otp-confirm verifies the OTP and then marks the email/msisdn
        // as verified on the user row. We need the user context to do that.
        g.MapPost("/otp-confirm", async (ConfirmOTPRequest req, OtpRepository repo,
            UserRepository users, HttpContext http, IOptions<DmartSettings> settings,
            CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            var ok = await repo.VerifyAndConsumeAsync(
                dest, req.Code, settings.Value.MaxOtpVerifyAttempts, ct);
            if (!ok)
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            // If the caller is authenticated, update their verified flags.
            var actor = http.Actor();
            if (actor is not null)
            {
                var user = await users.GetByShortnameAsync(actor, ct);
                if (user is not null)
                {
                    var updated = user with
                    {
                        IsEmailVerified = !string.IsNullOrEmpty(req.Email) || user.IsEmailVerified,
                        IsMsisdnVerified = !string.IsNullOrEmpty(req.Msisdn) || user.IsMsisdnVerified,
                        UpdatedAt = TimeUtils.Now(),
                    };
                    await users.UpsertAsync(updated, ct);
                }
            }
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");
    }

    // Reset OTPs are stored under a dedicated key prefix so the login OTP
    // path (which reads bare {dest}) can't consume them as a login credential.
    private const string ResetOtpPrefix = "pwd-reset:";
    internal static string ResetOtpKey(string dest) => ResetOtpPrefix + dest;
}
