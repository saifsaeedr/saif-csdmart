using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Utils;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

public sealed class UserService(
    UserRepository users,
    OtpRepository otp,
    PasswordHasher hasher,
    JwtIssuer jwt,
    InvitationJwt invitationJwt,
    InvitationRepository invitations,
    InvitationService invitationService,
    IOptions<DmartSettings> settings)
{
    // Management space name — comes from DmartSettings.ManagementSpace so the
    // caller can rename it uniformly via config. Default is "management".
    private string MgmtSpace => settings.Value.ManagementSpace;

    public Task<User?> GetByShortnameAsync(string shortname, CancellationToken ct = default)
        => users.GetByShortnameAsync(shortname, ct);

    public async Task<Result<(User User, Dictionary<string, string> Invitations)>> CreateAsync(
        string shortname, string? email, string? msisdn, string? password, string? language,
        string? emailOtp = null, string? msisdnOtp = null,
        CancellationToken ct = default)
    {
        // Python: check is_registrable setting before allowing self-registration.
        if (!settings.Value.IsRegistrable)
            return Result<(User, Dictionary<string, string>)>.Fail("forbidden", "registration is disabled");

        if (string.IsNullOrWhiteSpace(shortname))
            return Result<(User, Dictionary<string, string>)>.Fail("invalid_shortname", "shortname required");
        if (await users.ExistsAsync(shortname, email, msisdn, ct))
            return Result<(User, Dictionary<string, string>)>.Fail("conflict", "user already exists");

        // Python: is_otp_for_create_required — every supplied channel (email or
        // msisdn) must carry an OTP that was issued via /user/otp-request and
        // still valid. The OTP is consumed on success so it can't be reused.
        if (settings.Value.IsOtpForCreateRequired)
        {
            if (!string.IsNullOrEmpty(email))
            {
                if (string.IsNullOrEmpty(emailOtp))
                    return Result<(User, Dictionary<string, string>)>.Fail("bad_request", "email_otp is required");
                if (!await otp.VerifyAndConsumeAsync(email, emailOtp, ct))
                    return Result<(User, Dictionary<string, string>)>.Fail("invalid_otp", "email_otp is invalid or expired");
            }
            if (!string.IsNullOrEmpty(msisdn))
            {
                if (string.IsNullOrEmpty(msisdnOtp))
                    return Result<(User, Dictionary<string, string>)>.Fail("bad_request", "msisdn_otp is required");
                if (!await otp.VerifyAndConsumeAsync(msisdn, msisdnOtp, ct))
                    return Result<(User, Dictionary<string, string>)>.Fail("invalid_otp", "msisdn_otp is invalid or expired");
            }
        }

        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = MgmtSpace,
            Subpath = "users",
            OwnerShortname = shortname,
            Email = email,
            Msisdn = msisdn,
            Password = string.IsNullOrEmpty(password) ? null : hasher.Hash(password),
            Language = ParseLanguage(language),
            Type = UserType.Web,
            IsActive = true,
            // Mark channels as verified when they were proven via a valid OTP
            // during registration — saves the user a separate /otp-confirm round.
            IsEmailVerified = !string.IsNullOrEmpty(email) && settings.Value.IsOtpForCreateRequired,
            IsMsisdnVerified = !string.IsNullOrEmpty(msisdn) && settings.Value.IsOtpForCreateRequired,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user, ct);

        // Mint invitation JWTs for every unverified channel so the user can
        // complete first-login via POST /user/login (invitation path). When
        // IsOtpForCreateRequired=true, channels are already verified above and
        // no invitation is needed — matches Python's send_sms_email_invitation
        // which only triggers for unverified channels.
        var minted = new Dictionary<string, string>();
        if (!user.IsEmailVerified && !string.IsNullOrWhiteSpace(user.Email))
        {
            var t = await invitationService.MintAsync(user, InvitationChannel.Email, ct);
            if (t is not null) minted["email"] = t;
        }
        if (!user.IsMsisdnVerified && !string.IsNullOrWhiteSpace(user.Msisdn))
        {
            var t = await invitationService.MintAsync(user, InvitationChannel.Sms, ct);
            if (t is not null) minted["msisdn"] = t;
        }

        return Result<(User, Dictionary<string, string>)>.Ok((user, minted));
    }

    // Standard password-based login. Mirrors Python's login() PATH C (password).
    public async Task<Result<(string Access, string Refresh, User User)>> LoginAsync(
        UserLoginRequest req, Dictionary<string, string>? requestHeaders = null, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(req, ct);
        if (user is null)
            return Result<(string, string, User)>.Fail("not_found", "user not found");
        if (!user.IsActive)
            return Result<(string, string, User)>.Fail("inactive", "user is inactive");

        // Account lockout after max_failed_login_attempts (Python: handle_failed_login_attempt).
        var maxAttempts = settings.Value.MaxFailedLoginAttempts;
        if (maxAttempts > 0 && user.AttemptCount is not null && user.AttemptCount >= maxAttempts)
            return Result<(string, string, User)>.Fail("inactive", "account locked due to too many failed attempts");

        if (string.IsNullOrEmpty(user.Password) || req.Password is null
            || !hasher.Verify(req.Password, user.Password))
        {
            await users.IncrementAttemptAsync(user.Shortname, ct);
            return Result<(string, string, User)>.Fail("invalid_credentials", "incorrect password");
        }

        // Device lock check — applies regardless of user type.
        if (user.LockedToDevice && !string.IsNullOrEmpty(user.DeviceId)
            && (string.IsNullOrEmpty(req.DeviceId) || req.DeviceId != user.DeviceId))
        {
            return Result<(string, string, User)>.Fail("inactive", "account locked to a unique device");
        }
        // New device detection for mobile users (OTP required).
        if (user.Type == UserType.Mobile && !string.IsNullOrEmpty(user.DeviceId)
            && !string.IsNullOrEmpty(req.DeviceId) && req.DeviceId != user.DeviceId)
        {
            return Result<(string, string, User)>.Fail("invalid_otp", "new device detected, login with otp");
        }

        return await ProcessLoginAsync(user, req, requestHeaders, ct);
    }

    // OTP-based login. Mirrors Python's login() PATH B (OTP).
    public async Task<Result<(string Access, string Refresh, User User)>> LoginWithOtpAsync(
        UserLoginRequest req, Dictionary<string, string>? requestHeaders = null, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(req, ct);
        if (user is null)
            return Result<(string, string, User)>.Fail("not_found", "user not found");
        if (!user.IsActive)
            return Result<(string, string, User)>.Fail("inactive", "user is inactive");

        // Validate OTP code.
        var dest = user.Msisdn ?? user.Email ?? user.Shortname;
        if (string.IsNullOrEmpty(req.Otp) || !await otp.VerifyAndConsumeAsync(dest, req.Otp, ct))
            return Result<(string, string, User)>.Fail("invalid_otp", "invalid or expired OTP");

        // Python also optionally verifies password if provided alongside OTP.
        if (!string.IsNullOrEmpty(req.Password)
            && !string.IsNullOrEmpty(user.Password)
            && !hasher.Verify(req.Password, user.Password))
        {
            return Result<(string, string, User)>.Fail("invalid_credentials", "incorrect password");
        }

        return await ProcessLoginAsync(user, req, requestHeaders, ct);
    }

    // Invitation-token login. Mirrors Python's login() PATH A (invitation).
    //
    // Wire contract (Python parity):
    //   * Body carries `invitation` = full JWT string minted earlier. No password.
    //   * JWT payload is `{data:{shortname, channel}, expires:<unix>}` — see
    //     InvitationJwt for the exact encoding.
    //   * The JWT MUST correspond to a live row in the invitations table —
    //     replays after consumption fail with INVALID_INVITATION.
    //   * If the body includes shortname/email/msisdn cross-checks, at least
    //     one must match the user record resolved from the JWT's shortname claim.
    //   * On success: set ForcePasswordChange=true; set IsEmailVerified or
    //     IsMsisdnVerified based on the JWT's channel claim; delete the
    //     invitation row; issue access/refresh tokens via ProcessLoginAsync.
    public async Task<Result<(string Access, string Refresh, User User)>> LoginWithInvitationAsync(
        UserLoginRequest req, Dictionary<string, string>? requestHeaders = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Invitation))
            return Result<(string, string, User)>.Fail("invalid_invitation", "invitation token is required");

        if (!invitationJwt.TryVerify(req.Invitation, out var shortname, out var channel))
            return Result<(string, string, User)>.Fail("invalid_invitation", "invitation token is invalid or expired");

        // DB row is the single-use enforcement — a consumed or never-minted
        // JWT is rejected even if the signature and expiry check out.
        var storedValue = await invitations.GetValueAsync(req.Invitation, ct);
        if (storedValue is null)
            return Result<(string, string, User)>.Fail("invalid_invitation", "invitation token is invalid or expired");

        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is null)
            return Result<(string, string, User)>.Fail("invalid_invitation", "invitation token is invalid or expired");
        if (!user.IsActive)
            return Result<(string, string, User)>.Fail("inactive", "user is inactive");

        // Optional body-level cross-check: if the client passed shortname/email/msisdn
        // alongside the JWT, at least one must line up with the resolved user.
        var anyMatch =
               (req.Shortname is not null && string.Equals(req.Shortname, user.Shortname, StringComparison.Ordinal))
            || (req.Email is not null && string.Equals(req.Email, user.Email, StringComparison.OrdinalIgnoreCase))
            || (req.Msisdn is not null && string.Equals(req.Msisdn, user.Msisdn, StringComparison.Ordinal));
        var anyProvided = req.Shortname is not null || req.Email is not null || req.Msisdn is not null;
        if (anyProvided && !anyMatch)
            return Result<(string, string, User)>.Fail("invalid_invitation", "invitation does not match the supplied identity");

        // Device lock checks — same enforcement as password login.
        if (user.LockedToDevice && !string.IsNullOrEmpty(user.DeviceId)
            && (string.IsNullOrEmpty(req.DeviceId) || req.DeviceId != user.DeviceId))
        {
            return Result<(string, string, User)>.Fail("inactive", "account locked to a unique device");
        }

        var updated = user with
        {
            ForcePasswordChange = true,
            IsEmailVerified = channel == InvitationChannel.Email ? true : user.IsEmailVerified,
            IsMsisdnVerified = channel == InvitationChannel.Sms   ? true : user.IsMsisdnVerified,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(updated, ct);

        // Single-use — delete BEFORE ProcessLoginAsync so a crash in session
        // setup still consumes the invitation (safer to re-auth with a new
        // invitation than to allow a replay).
        await invitations.DeleteAsync(req.Invitation, ct);

        return await ProcessLoginAsync(updated, req, requestHeaders, ct);
    }

    // Shared post-authentication flow. Mirrors Python's process_user_login().
    private async Task<Result<(string Access, string Refresh, User User)>> ProcessLoginAsync(
        User user, UserLoginRequest req,
        Dictionary<string, string>? requestHeaders, CancellationToken ct)
    {
        await users.ResetAttemptsAsync(user.Shortname, ct);

        // Python: max_sessions_per_user enforcement — check session count before
        // creating a new one. If at capacity, the oldest session should be evicted
        // or the login should fail. Python's get_user_session checks the count;
        // we limit by deleting oldest sessions when over the limit.
        var maxSessions = settings.Value.MaxSessionsPerUser;
        if (maxSessions > 0)
        {
            // Evict excess sessions (keep newest maxSessions-1 to make room for the new one)
            await users.EvictExcessSessionsAsync(user.Shortname, maxSessions - 1, ct);
        }

        // Persist device_id if changed.
        var updatedUser = user;
        var needsUpdate = false;
        if (!string.IsNullOrEmpty(req.DeviceId) && req.DeviceId != user.DeviceId)
        {
            updatedUser = updatedUser with { DeviceId = req.DeviceId };
            needsUpdate = true;
        }

        // Python tracks last_login = {timestamp, headers} on every successful login.
        if (requestHeaders is not null)
        {
            var loginInfo = new Dictionary<string, object>
            {
                // Cast to int so source-gen JSON can serialize it (no Int64 TypeInfo).
                ["timestamp"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["headers"] = requestHeaders,
            };
            updatedUser = updatedUser with { LastLogin = loginInfo };
            needsUpdate = true;
        }

        if (needsUpdate)
        {
            updatedUser = updatedUser with { UpdatedAt = DateTime.UtcNow };
            await users.UpsertAsync(updatedUser, ct);
        }

        var access = jwt.IssueAccess(updatedUser.Shortname, updatedUser.Roles, updatedUser.Type);
        var refresh = jwt.IssueRefresh(updatedUser.Shortname, updatedUser.Type);

        // Create session row (Python: db.set_user_session). If the client
        // supplied a firebase_token on the login body, persist it on the
        // session row so a future push plugin can discover it via
        // UserRepository.GetSessionFirebaseTokensAsync. Python parity.
        await users.CreateSessionAsync(updatedUser.Shortname, access, req.FirebaseToken, ct);

        return Result<(string, string, User)>.Ok((access, refresh, updatedUser));
    }

    // Validate a password against the stored hash (Python: POST /validate_password).
    public async Task<bool> ValidatePasswordAsync(string shortname, string password, CancellationToken ct = default)
    {
        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is null || string.IsNullOrEmpty(user.Password)) return false;
        return hasher.Verify(password, user.Password);
    }

    public async Task<Result<User>> UpdateProfileAsync(
        string shortname, Dictionary<string, object> patch,
        string? sessionToken = null, CancellationToken ct = default)
    {
        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is null) return Result<User>.Fail("not_found", "user missing");

        // Reject patches targeting protected payload fields.
        var protectedCsv = settings.Value.UserProfilePayloadProtectedFields;
        if (!string.IsNullOrWhiteSpace(protectedCsv))
        {
            var protectedFields = protectedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var key in patch.Keys)
            {
                if (protectedFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                    return Result<User>.Fail("bad_request", $"field '{key}' is protected and cannot be updated");
            }
        }

        // Password change: Python requires old_password unless force_password_change.
        string? newPasswordHash = null;
        if (patch.TryGetValue("password", out var pwObj) && pwObj is not null)
        {
            var newPw = pwObj.ToString();
            if (!user.ForcePasswordChange)
            {
                if (!patch.TryGetValue("old_password", out var oldPwObj) || oldPwObj is null)
                    return Result<User>.Fail("bad_request", "old_password required to change password");
                if (string.IsNullOrEmpty(user.Password) || !hasher.Verify(oldPwObj.ToString()!, user.Password))
                    return Result<User>.Fail("invalid_credentials", "old password is incorrect");
            }
            newPasswordHash = string.IsNullOrEmpty(newPw) ? null : hasher.Hash(newPw!);
        }

        static string? Str(Dictionary<string, object> d, string k, string? fallback)
            => d.TryGetValue(k, out var v) ? v?.ToString() : fallback;

        // If the password was updated, clear the force-change flag — Python
        // does the same inside set_user_profile. The flag is only meaningful
        // until the user picks their own password; keeping it true afterwards
        // would force them to change it again on next login.
        bool resolvedForcePasswordChange;
        if (newPasswordHash is not null)
        {
            resolvedForcePasswordChange = false;
        }
        else if (patch.TryGetValue("force_password_change", out var fpc))
        {
            resolvedForcePasswordChange = fpc is true || (fpc is bool b && b);
        }
        else
        {
            resolvedForcePasswordChange = user.ForcePasswordChange;
        }

        var updated = user with
        {
            Email = Str(patch, "email", user.Email),
            Msisdn = Str(patch, "msisdn", user.Msisdn),
            Language = patch.TryGetValue("language", out var l) && l is not null
                ? ParseLanguage(l.ToString())
                : user.Language,
            Displayname = patch.TryGetValue("displayname", out var dn) && dn is not null
                ? new Translation(En: dn.ToString())
                : user.Displayname,
            Description = patch.TryGetValue("description", out var desc) && desc is not null
                ? new Translation(En: desc.ToString())
                : user.Description,
            DeviceId = Str(patch, "device_id", user.DeviceId),
            ForcePasswordChange = resolvedForcePasswordChange,
            Password = newPasswordHash ?? user.Password,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(updated, ct);

        // Python: if password changed and logout_on_pwd_change, delete all sessions.
        if (newPasswordHash is not null && settings.Value.LogoutOnPwdChange)
            await users.DeleteAllSessionsAsync(shortname, ct);

        // Python: if is_active set to false, delete all sessions.
        if (patch.TryGetValue("is_active", out var ia) && ia is false)
            await users.DeleteAllSessionsAsync(shortname, ct);

        // Python parity: `firebase_token` on the patch body writes onto the
        // caller's CURRENT session row (matched by shortname+token), not onto
        // every session. Without the caller's token we can't identify the
        // session, so a missing sessionToken is just a silent skip — same
        // outcome as Python when no auth_token is threaded through.
        if (patch.TryGetValue("firebase_token", out var ft) && ft is not null
            && !string.IsNullOrEmpty(sessionToken))
        {
            var ftStr = ft.ToString();
            if (!string.IsNullOrEmpty(ftStr))
                await users.UpdateSessionFirebaseTokenAsync(shortname, sessionToken, ftStr, ct);
        }

        return Result<User>.Ok(updated);
    }

    public async Task DeleteAsync(string shortname, CancellationToken ct = default)
    {
        // Python: remove all sessions before deleting user.
        await users.DeleteAllSessionsAsync(shortname, ct);
        await users.DeleteAsync(shortname, ct);
    }

    public async Task LogoutAsync(string? token, CancellationToken ct = default)
    {
        // Python: db.remove_user_session() — delete the specific session row.
        if (!string.IsNullOrEmpty(token))
            await users.DeleteSessionAsync(token, ct);
    }

    private async Task<User?> ResolveUserAsync(UserLoginRequest req, CancellationToken ct)
    {
        return req.Shortname is not null ? await users.GetByShortnameAsync(req.Shortname, ct)
             : req.Email is not null     ? await users.GetByEmailAsync(req.Email, ct)
             : req.Msisdn is not null    ? await users.GetByMsisdnAsync(req.Msisdn, ct)
             : null;
    }

    private static Language ParseLanguage(string? code) => code?.ToLowerInvariant() switch
    {
        "ar" or "arabic"  => Language.Ar,
        "ku" or "kurdish" => Language.Ku,
        "fr" or "french"  => Language.Fr,
        "tr" or "turkish" => Language.Tr,
        _                 => Language.En,
    };
}
