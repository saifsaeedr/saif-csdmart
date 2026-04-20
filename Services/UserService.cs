using System.Text.Json;
using System.Text.RegularExpressions;
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
    IOptions<DmartSettings> settings)
{
    // Management space name — comes from DmartSettings.ManagementSpace so the
    // caller can rename it uniformly via config. Default is "management".
    private string MgmtSpace => settings.Value.ManagementSpace;

    public Task<User?> GetByShortnameAsync(string shortname, CancellationToken ct = default)
        => users.GetByShortnameAsync(shortname, ct);

    // Python-parity password regex (utils/regex.py::PASSWORD). Requires at
    // least one digit (Latin or Arabic-Indic), one uppercase letter (Latin
    // A-Z or Arabic ا-ي), length 8-64 from a specific character class.
    private const string PasswordPattern =
        "^(?=.*[0-9\u0660-\u0669])(?=.*[A-Z\u0621-\u064a])" +
        "[a-zA-Z\u0621-\u064a0-9\u0660-\u0669 _#@%*!?$^&()+={}\\[\\]~|;:,.<>/-]{8,64}$";

    // Python's /user/create takes a core.Record body and returns a Record with
    // {access_token, type} — i.e. it auto-logs-in the new user. This mirrors
    // that flow:
    //   1. Validate is_registrable + email/msisdn + OTPs + password regex
    //   2. Verify OTPs via peek (Python's verify_user doesn't consume)
    //   3. Build User from rec.Attributes (email, msisdn, password, roles,
    //      displayname, description, payload, language)
    //   4. Persist + auto-login (issue access/refresh + create session row)
    public async Task<Result<(User User, string Access, string Refresh)>> CreateAsync(
        Record rec, Dictionary<string, string>? requestHeaders = null,
        CancellationToken ct = default)
    {
        var s = settings.Value;
        if (!s.IsRegistrable)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.SESSION, "Register API is disabled", "create");

        if (string.IsNullOrWhiteSpace(rec.Shortname))
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.INVALID_IDENTIFIER, "shortname required", "request");

        var attrs = rec.Attributes ?? new();
        var email = ConvertToString(attrs.GetValueOrDefault("email"))?.ToLowerInvariant();
        var msisdn = ConvertToString(attrs.GetValueOrDefault("msisdn"));
        var password = ConvertToString(attrs.GetValueOrDefault("password"));
        var emailOtp = ConvertToString(attrs.GetValueOrDefault("email_otp"));
        var msisdnOtp = ConvertToString(attrs.GetValueOrDefault("msisdn_otp"));

        // Python-parity validation chain (last-match wins — mirrors the
        // sequential `validation_message = …` assignments in router.py).
        string? validationMessage = null;
        if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(msisdn))
            validationMessage = "Email or MSISDN is required";
        if (!string.IsNullOrEmpty(email) && s.IsOtpForCreateRequired && string.IsNullOrEmpty(emailOtp))
            validationMessage = "Email OTP is required";
        if (!string.IsNullOrEmpty(msisdn) && s.IsOtpForCreateRequired && string.IsNullOrEmpty(msisdnOtp))
            validationMessage = "MSISDN OTP is required";
        if (!string.IsNullOrEmpty(password) && !Regex.IsMatch(password, PasswordPattern))
            validationMessage = "password dose not match required rules";
        if (validationMessage is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.SESSION, validationMessage, "create");

        if (await users.ExistsAsync(rec.Shortname, email, msisdn, ct))
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.SHORTNAME_ALREADY_EXIST, "user already exists", "db");

        // OTP verification (Python uses verify_user = peek; OTP is NOT consumed
        // so a subsequent /otp-confirm can still use it). Skipped entirely when
        // is_otp_for_create_required=false — both channels are then treated as
        // verified by fiat, mirroring Python's `is_valid_otp = True` branch.
        var emailVerified = false;
        var msisdnVerified = false;
        if (!string.IsNullOrEmpty(msisdn))
        {
            if (s.IsOtpForCreateRequired)
            {
                var stored = await otp.GetCodeAsync(msisdn, ct);
                if (stored is null || stored != msisdnOtp)
                    return Result<(User, string, string)>.Fail(
                        InternalErrorCode.SESSION, "Invalid MSISDN OTP", "create");
            }
            msisdnVerified = true;
        }
        if (!string.IsNullOrEmpty(email))
        {
            if (s.IsOtpForCreateRequired)
            {
                var stored = await otp.GetCodeAsync(email, ct);
                if (stored is null || stored != emailOtp)
                    return Result<(User, string, string)>.Fail(
                        InternalErrorCode.SESSION, "Invalid Email OTP", "create");
            }
            emailVerified = true;
        }

        var rolesList = ExtractStringList(attrs, "roles");
        var groupsList = ExtractStringList(attrs, "groups");
        var language = ParseLanguage(ConvertToString(attrs.GetValueOrDefault("language")));
        var displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : null;
        var description = attrs.TryGetValue("description", out var desc) ? ParseTranslation(desc) : null;
        var payload = ExtractPayload(attrs);

        var user = new User
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = MgmtSpace,
            Subpath = "users",
            OwnerShortname = "dmart",
            Email = email,
            Msisdn = msisdn,
            Password = string.IsNullOrEmpty(password) ? null : hasher.Hash(password),
            Language = language,
            Displayname = displayname,
            Description = description,
            Payload = payload,
            Roles = rolesList ?? new(),
            Groups = groupsList ?? new(),
            Type = UserType.Web,
            IsActive = true,
            IsEmailVerified = emailVerified,
            IsMsisdnVerified = msisdnVerified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user, ct);

        // Auto-login (Python: process_user_login at the end of create_user).
        var access = jwt.IssueAccess(user.Shortname, user.Roles, user.Type);
        var refresh = jwt.IssueRefresh(user.Shortname, user.Type);
        await users.CreateSessionAsync(user.Shortname, access, null, ct);

        if (requestHeaders is not null)
        {
            var loginInfo = new Dictionary<string, object>
            {
                ["timestamp"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["headers"] = requestHeaders,
            };
            var updated = user with { LastLogin = loginInfo, UpdatedAt = DateTime.UtcNow };
            await users.UpsertAsync(updated, ct);
            user = updated;
        }

        return Result<(User, string, string)>.Ok((user, access, refresh));
    }

    private static string? ConvertToString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null => null,
            _ => el.GetRawText(),
        },
        _ => v.ToString(),
    };

    private static List<string>? ExtractStringList(Dictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var raw) || raw is null) return null;
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            return list;
        }
        if (raw is List<string> stringList) return stringList;
        if (raw is IEnumerable<object> objs)
            return objs.Select(o => o?.ToString() ?? "").ToList();
        return null;
    }

    private static Translation? ParseTranslation(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                // Empty object → null, not an empty Translation (matches what
                // Python's `{}` passes through — no localized strings set).
                if (!el.EnumerateObject().Any()) return null;
                return new Translation(
                    En: el.TryGetProperty("en", out var en) ? en.GetString() : null,
                    Ar: el.TryGetProperty("ar", out var ar) ? ar.GetString() : null,
                    Ku: el.TryGetProperty("ku", out var ku) ? ku.GetString() : null);
            }
            if (el.ValueKind == JsonValueKind.String) return new Translation(En: el.GetString());
            return null;
        }
        return new Translation(En: value.ToString());
    }

    private static Payload? ExtractPayload(Dictionary<string, object> attrs)
    {
        if (!attrs.TryGetValue("payload", out var raw) || raw is null) return null;
        if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return new Payload
        {
            ContentType = el.TryGetProperty("content_type", out var ct)
                && ct.ValueKind == JsonValueKind.String
                && Enum.TryParse<ContentType>(ct.GetString(), true, out var cte)
                    ? cte : ContentType.Json,
            SchemaShortname = el.TryGetProperty("schema_shortname", out var ss)
                && ss.ValueKind == JsonValueKind.String ? ss.GetString() : null,
            Body = el.TryGetProperty("body", out var b) ? b.Clone() : null,
        };
    }

    // Standard password-based login. Mirrors Python's login() PATH C (password).
    public async Task<Result<(string Access, string Refresh, User User)>> LoginAsync(
        UserLoginRequest req, Dictionary<string, string>? requestHeaders = null, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(req, ct);
        if (user is null)
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.USERNAME_NOT_EXIST, "Invalid username or password", "auth");

        // Pre-check: attempt_count >= max means a prior run of
        // HandleFailedLoginAttemptAsync already locked the account (or an
        // admin/test set the counter directly). Reject even a correct password
        // before we look at is_active, so the caller sees USER_ACCOUNT_LOCKED
        // rather than USER_ISNT_VERIFIED for an auto-locked user.
        var maxAttempts = settings.Value.MaxFailedLoginAttempts;
        if (maxAttempts > 0 && user.AttemptCount is int count && count >= maxAttempts)
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.USER_ACCOUNT_LOCKED,
                "Account has been locked due to too many failed login attempts.", "auth");

        if (RejectIfNotActive(user) is { } inactiveReject) return inactiveReject;

        if (string.IsNullOrEmpty(user.Password) || req.Password is null
            || !hasher.Verify(req.Password, user.Password))
        {
            var locked = await HandleFailedLoginAttemptAsync(user, ct);
            return locked
                ? Result<(string, string, User)>.Fail(
                    InternalErrorCode.USER_ACCOUNT_LOCKED,
                    "Account has been locked due to too many failed login attempts.", "auth")
                : Result<(string, string, User)>.Fail(
                    InternalErrorCode.PASSWORD_NOT_VALIDATED, "Invalid username or password", "auth");
        }

        // Device lock check — applies regardless of user type.
        if (user.LockedToDevice && !string.IsNullOrEmpty(user.DeviceId)
            && (string.IsNullOrEmpty(req.DeviceId) || req.DeviceId != user.DeviceId))
        {
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.USER_ACCOUNT_LOCKED,
                "This account is locked to a unique device !", "auth");
        }
        // New device detection for mobile users (OTP required). Python uses
        // OTP_NEEDED (115) — clients inspect the numeric code to route the
        // user to the OTP screen on first-device login.
        if (user.Type == UserType.Mobile && !string.IsNullOrEmpty(user.DeviceId)
            && !string.IsNullOrEmpty(req.DeviceId) && req.DeviceId != user.DeviceId)
        {
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.OTP_NEEDED, "New device detected, login with otp", "auth");
        }

        return await ProcessLoginAsync(user, req, requestHeaders, ct);
    }

    // OTP-based login. Mirrors Python's login() PATH B (OTP).
    public async Task<Result<(string Access, string Refresh, User User)>> LoginWithOtpAsync(
        UserLoginRequest req, Dictionary<string, string>? requestHeaders = null, CancellationToken ct = default)
    {
        // Python parity: OTP login must carry exactly one identifier.
        var identifierCount = (req.Shortname is not null ? 1 : 0)
                            + (req.Email is not null ? 1 : 0)
                            + (req.Msisdn is not null ? 1 : 0);
        if (identifierCount > 1)
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.OTP_ISSUE,
                "Provide either msisdn, email or shortname, not both.", "auth");
        if (identifierCount == 0)
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.OTP_ISSUE,
                "Either msisdn, email or shortname must be provided.", "auth");

        var user = await ResolveUserAsync(req, ct);
        if (user is null)
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.USERNAME_NOT_EXIST, "Invalid username or password", "auth");
        if (RejectIfNotActive(user) is { } inactiveReject) return inactiveReject;

        // Validate OTP code.
        // Python parity: key is derived from the REQUEST identifier, not the
        // user record. When the caller sent `shortname`, Python falls back to
        // `user.msisdn` because /otp-request-login writes to msisdn for the
        // shortname path — same scheme here.
        var dest = !string.IsNullOrEmpty(req.Shortname)
            ? user.Msisdn
            : (req.Msisdn ?? req.Email?.ToLowerInvariant());
        if (string.IsNullOrEmpty(dest) || string.IsNullOrEmpty(req.Otp)
            || !await otp.VerifyAndConsumeAsync(dest, req.Otp, ct))
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.OTP_INVALID, "Wrong OTP", "auth");

        // Python also optionally verifies password if provided alongside OTP.
        if (!string.IsNullOrEmpty(req.Password)
            && !string.IsNullOrEmpty(user.Password)
            && !hasher.Verify(req.Password, user.Password))
        {
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.PASSWORD_NOT_VALIDATED, "Invalid username or password", "auth");
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
        // Python emits `type=jwtauth` for every invitation failure so clients
        // can distinguish token problems from plain auth failures.
        if (string.IsNullOrWhiteSpace(req.Invitation))
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.INVALID_INVITATION, "Expired or invalid invitation", "jwtauth");

        if (!invitationJwt.TryVerify(req.Invitation, out var shortname, out var channel))
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.INVALID_INVITATION, "Expired or invalid invitation", "jwtauth");

        // DB row is the single-use enforcement — a consumed or never-minted
        // JWT is rejected even if the signature and expiry check out.
        var storedValue = await invitations.GetValueAsync(req.Invitation, ct);
        if (storedValue is null)
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.INVALID_INVITATION, "Expired or invalid invitation", "jwtauth");

        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is null)
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.INVALID_INVITATION, "Expired or invalid invitation", "jwtauth");
        if (RejectIfNotActive(user) is { } inactiveReject) return inactiveReject;

        // Optional body-level cross-check: if the client passed shortname/email/msisdn
        // alongside the JWT, at least one must line up with the resolved user.
        var anyMatch =
               (req.Shortname is not null && string.Equals(req.Shortname, user.Shortname, StringComparison.Ordinal))
            || (req.Email is not null && string.Equals(req.Email, user.Email, StringComparison.OrdinalIgnoreCase))
            || (req.Msisdn is not null && string.Equals(req.Msisdn, user.Msisdn, StringComparison.Ordinal));
        var anyProvided = req.Shortname is not null || req.Email is not null || req.Msisdn is not null;
        if (anyProvided && !anyMatch)
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.INVALID_INVITATION, "Invalid invitation or data provided", "jwtauth");

        // Device lock checks — same enforcement as password login.
        if (user.LockedToDevice && !string.IsNullOrEmpty(user.DeviceId)
            && (string.IsNullOrEmpty(req.DeviceId) || req.DeviceId != user.DeviceId))
        {
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.USER_ACCOUNT_LOCKED,
                "This account is locked to a unique device !", "auth");
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

    // Shared inactive-user gate for LoginAsync / LoginWithOtpAsync /
    // LoginWithInvitationAsync. Returns a pre-built rejection Result when
    // the account is deactivated (user was never verified, or was manually
    // disabled), null when the user can proceed. Centralizing the message
    // prevents drift between the three login paths — clients branch on
    // the exact string via tsdmart.
    private static Result<(string Access, string Refresh, User User)>? RejectIfNotActive(User user)
        => user.IsActive
            ? null
            : Result<(string, string, User)>.Fail(
                InternalErrorCode.USER_ISNT_VERIFIED, "This user is not verified", "auth");

    private async Task<bool> HandleFailedLoginAttemptAsync(User user, CancellationToken ct)
    {
        await users.IncrementAttemptAsync(user.Shortname, ct);

        var maxAttempts = settings.Value.MaxFailedLoginAttempts;
        if (maxAttempts <= 0) return false;

        // Load fresh — the attempt count in `user` is pre-increment and stale.
        var refreshed = await users.GetByShortnameAsync(user.Shortname, ct);
        if (refreshed is null) return false;
        if (refreshed.AttemptCount is not int count || count < maxAttempts) return false;
        if (!refreshed.IsActive) return true; // already locked by a prior attempt

        var locked = refreshed with { IsActive = false, UpdatedAt = DateTime.UtcNow };
        await users.UpsertAsync(locked, ct);
        // Python: db.remove_user_session(shortname) — every active session is
        // invalidated so an already-logged-in tab can't keep making requests
        // after the account is auto-disabled.
        await users.DeleteAllSessionsAsync(user.Shortname, ct);
        return true;
    }

    // Shared post-authentication flow. Mirrors Python's process_user_login().
    // Internal rather than private: OAuth handlers (GoogleProvider / Facebook /
    // Apple) resolve a User on their own, then need to issue session + JWT
    // through the same code path as password/OTP/invitation login. Keeping it
    // internal localizes exposure to the assembly while allowing reuse.
    internal async Task<Result<(string Access, string Refresh, User User)>> ProcessLoginAsync(
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

        // Sync the in-memory copy with ResetAttemptsAsync — without this, a
        // later UpsertAsync (triggered by device_id or last_login changes)
        // would restore the stale pre-login attempt_count via
        // `attempt_count = EXCLUDED.attempt_count` in the ON CONFLICT clause.
        var updatedUser = user with { AttemptCount = 0 };
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
        if (user is null)
            return Result<User>.Fail(
                InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user missing", "db");

        // Reject patches targeting protected payload fields.
        var protectedCsv = settings.Value.UserProfilePayloadProtectedFields;
        if (!string.IsNullOrWhiteSpace(protectedCsv))
        {
            var protectedFields = protectedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var key in patch.Keys)
            {
                if (protectedFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                    return Result<User>.Fail(
                        InternalErrorCode.PROTECTED_FIELD,
                        $"field '{key}' is protected and cannot be updated", "request");
            }
        }

        // Password change: Python requires old_password unless force_password_change.
        string? newPasswordHash = null;
        if (patch.TryGetValue("password", out var pwObj) && pwObj is not null)
        {
            var newPw = pwObj.ToString();
            // Python parity: profile endpoint rejects a password that fails
            // the PASSWORD regex with INVALID_PASSWORD_RULES under type=jwtauth.
            if (!string.IsNullOrEmpty(newPw) && !Auth.PasswordRules.IsValid(newPw))
                return Result<User>.Fail(
                    InternalErrorCode.INVALID_PASSWORD_RULES, "Invalid username or password", "jwtauth");
            if (!user.ForcePasswordChange)
            {
                // Python parity (api/user/router.py:665-682, set_user_profile):
                //   * missing old_password   → 403 PASSWORD_RESET_ERROR, type=auth,
                //     message "Wrong password have been provided!"
                //   * old_password mismatch  → 401 UNMATCHED_DATA, type=request,
                //     message "mismatch with the information provided"
                if (!patch.TryGetValue("old_password", out var oldPwObj) || oldPwObj is null)
                    return Result<User>.Fail(
                        InternalErrorCode.PASSWORD_RESET_ERROR,
                        "Wrong password have been provided!", "auth");
                if (string.IsNullOrEmpty(user.Password) || !hasher.Verify(oldPwObj.ToString()!, user.Password))
                    return Result<User>.Fail(
                        InternalErrorCode.UNMATCHED_DATA,
                        "mismatch with the information provided", "request");
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
