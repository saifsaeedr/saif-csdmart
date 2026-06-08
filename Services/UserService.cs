using System.Text.Json;
using System.Text.RegularExpressions;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

public sealed class UserService(
    UserRepository users,
    OtpRepository otp,
    PasswordHasher hasher,
    JwtIssuer jwt,
    HistoryRepository history,
    PluginManager plugins,
    IOptions<DmartSettings> settings,
    ILogger<UserService> log)
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
                InternalErrorCode.SESSION, "Register API is disabled", ErrorTypes.Create);

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
                InternalErrorCode.SESSION, validationMessage, ErrorTypes.Create);

        // Shortname conflict → SHORTNAME_ALREADY_EXIST (400). Self-registration
        // server-allocates the shortname (RegistrationHandler sends "auto") so
        // this never trips here; kept for any caller that supplies one. The
        // email/msisdn uniqueness check is deliberately deferred until AFTER OTP
        // verification below — see the anti-enumeration note there.
        if (!string.IsNullOrWhiteSpace(rec.Shortname)
            && await users.GetByShortnameAsync(rec.Shortname, ct) is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.SHORTNAME_ALREADY_EXIST, "already exists", ErrorTypes.Create);

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
                        InternalErrorCode.SESSION, "Invalid MSISDN OTP", ErrorTypes.Create);
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
                        InternalErrorCode.SESSION, "Invalid Email OTP", ErrorTypes.Create);
            }
            emailVerified = true;
        }

        // Anti-enumeration: the email/msisdn uniqueness check runs ONLY after
        // OTP verification. A caller who doesn't control the address can't
        // produce its OTP, so they get the generic "Invalid OTP" error instead
        // of a "@email:<value> already exists" existence oracle; a caller who
        // does control it only learns about their own address. (Python ran this
        // before OTP — we intentionally diverge to close the enumeration.) When
        // is_otp_for_create_required=false the OTP gate is a no-op, so the
        // surfaced DATA_SHOULD_BE_UNIQUE error is unchanged for that config.
        if (!string.IsNullOrEmpty(email) && await users.GetByEmailAsync(email, ct) is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                $"Entry properties should be unique: @email:{email} ", ErrorTypes.Request);
        if (!string.IsNullOrEmpty(msisdn) && await users.GetByMsisdnAsync(msisdn, ct) is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                $"Entry properties should be unique: @msisdn:{msisdn} ", ErrorTypes.Request);

        // Self-registration must not let a user grant themselves access: the
        // `roles`/`groups` attributes in the request body are deliberately
        // ignored here. A user cannot set nor change their own role/group.
        // Instead, when configured, every self-created user gets exactly the
        // single default role/group from settings; otherwise the list starts
        // empty. (The managed/admin create path is unaffected — an authorized
        // admin may still assign roles/groups there.)
        var rolesList = DefaultAccessOrEmpty(s.UserCreateDefaultRole);
        var groupsList = DefaultAccessOrEmpty(s.UserCreateDefaultGroup);
        var language = ParseLanguage(ConvertToString(attrs.GetValueOrDefault("language")));
        var displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : null;
        var description = attrs.TryGetValue("description", out var desc) ? ParseTranslation(desc) : null;
        var payload = ExtractPayload(attrs);

        var user = new User
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = MgmtSpace,
            // Canonical persisted form is the leading-slash variant so a
            // query like `Subpath = "/users"` matches both bootstrap admin
            // (AdminBootstrap.cs:74) and self-registered users. Without the
            // slash, /managed/query for /users returned only the admin.
            Subpath = "/users",
            OwnerShortname = "dmart",
            Email = email,
            Msisdn = msisdn,
            Password = string.IsNullOrEmpty(password) ? null : hasher.Hash(password),
            Language = language,
            Displayname = displayname,
            Description = description,
            Payload = payload,
            Roles = rolesList,
            Groups = groupsList,
            Type = UserType.Web,
            IsActive = true,
            IsEmailVerified = emailVerified,
            IsMsisdnVerified = msisdnVerified,
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
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
                ["timestamp"] = (int)DateTimeOffset.Now.ToUnixTimeSeconds(),
                ["headers"] = requestHeaders,
            };
            var updated = user with { LastLogin = loginInfo, UpdatedAt = TimeUtils.Now() };
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

    // Translate a configured default role/group into the new user's access
    // list: a configured value becomes the sole entry, a blank/whitespace
    // setting means "no default" → empty list. Trimmed so a stray-space config
    // value (e.g. "viewer ") doesn't mint a role name that never resolves.
    private static List<string> DefaultAccessOrEmpty(string? configured)
        => string.IsNullOrWhiteSpace(configured)
            ? new List<string>()
            : new List<string> { configured.Trim() };

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
                InternalErrorCode.INVALID_USERNAME_AND_PASS, "Invalid username or password", ErrorTypes.Auth);

        var (attemptLocked, unlockedUser) = await RejectIfAttemptLockedAsync(user, ct);
        if (attemptLocked is { } al) return al;
        user = unlockedUser; // possibly auto-unlocked after the cool-down
        if (RejectIfNotActive(user) is { } inactiveReject) return inactiveReject;

        if (string.IsNullOrEmpty(user.Password) || req.Password is null
            || !hasher.Verify(req.Password, user.Password))
        {
            var locked = await HandleFailedLoginAttemptAsync(user, ct);
            return locked
                ? Result<(string, string, User)>.Fail(
                    InternalErrorCode.USER_ACCOUNT_LOCKED,
                    "Account has been locked due to too many failed login attempts.", ErrorTypes.Auth)
                // Python returns INVALID_USERNAME_AND_PASS(10) for BOTH "no
                // user" and "wrong password" to avoid username enumeration.
                // Previously C# surfaced PASSWORD_NOT_VALIDATED(13) here which
                // lets callers tell the two apart — parity gap.
                : Result<(string, string, User)>.Fail(
                    InternalErrorCode.INVALID_USERNAME_AND_PASS, "Invalid username or password", ErrorTypes.Auth);
        }

        // Device lock check — applies regardless of user type.
        if (user.LockedToDevice && !string.IsNullOrEmpty(user.DeviceId)
            && (string.IsNullOrEmpty(req.DeviceId) || req.DeviceId != user.DeviceId))
        {
            return Result<(string, string, User)>.Fail(
                InternalErrorCode.USER_ACCOUNT_LOCKED,
                "This account is locked to a unique device !", ErrorTypes.Auth);
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
                InternalErrorCode.INVALID_USERNAME_AND_PASS, "Invalid username or password", ErrorTypes.Auth);

        var (attemptLocked, unlockedUser) = await RejectIfAttemptLockedAsync(user, ct);
        if (attemptLocked is { } al) return al;
        user = unlockedUser; // possibly auto-unlocked after the cool-down
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
        {
            // Wrong OTP counts as a failed login attempt. Keeps the lock-out
            // promise intact — without this, an attacker who guessed a valid
            // identifier could brute-force the 6-digit code without ever
            // tripping the threshold.
            var locked = await HandleFailedLoginAttemptAsync(user, ct);
            return locked
                ? Result<(string, string, User)>.Fail(
                    InternalErrorCode.USER_ACCOUNT_LOCKED,
                    "Account has been locked due to too many failed login attempts.", ErrorTypes.Auth)
                : Result<(string, string, User)>.Fail(
                    InternalErrorCode.OTP_INVALID, "Wrong OTP", ErrorTypes.Auth);
        }

        // Python also optionally verifies password if provided alongside OTP.
        if (!string.IsNullOrEmpty(req.Password)
            && !string.IsNullOrEmpty(user.Password)
            && !hasher.Verify(req.Password, user.Password))
        {
            var locked = await HandleFailedLoginAttemptAsync(user, ct);
            return locked
                ? Result<(string, string, User)>.Fail(
                    InternalErrorCode.USER_ACCOUNT_LOCKED,
                    "Account has been locked due to too many failed login attempts.", ErrorTypes.Auth)
                : Result<(string, string, User)>.Fail(
                    InternalErrorCode.PASSWORD_NOT_VALIDATED, "Invalid username or password", ErrorTypes.Auth);
        }

        return await ProcessLoginAsync(user, req, requestHeaders, ct);
    }

    // Shared inactive-user gate for LoginAsync / LoginWithOtpAsync.
    // Returns a pre-built rejection Result when
    // the account is deactivated (user was never verified, or was manually
    // disabled), null when the user can proceed. Centralizing the message
    // prevents drift between the two login paths — clients branch on
    // the exact string via tsdmart.
    // Python parity: `api/user/router.py::login` raises
    // USER_ACCOUNT_LOCKED(110) "Account has been locked." when is_active=false
    // (router.py:504-508). USER_ISNT_VERIFIED is a separate code Python uses
    // only on the verify-otp / registration flow — not on login. Callers that
    // still want the "verified" distinction live outside this helper.
    private static Result<(string Access, string Refresh, User User)>? RejectIfNotActive(User user)
        => user.IsActive
            ? null
            : Result<(string, string, User)>.Fail(
                InternalErrorCode.USER_ACCOUNT_LOCKED, "Account has been locked.", ErrorTypes.Auth);

    // Mirrors RejectIfNotActive but for the auto-lockout counter — surfaces
    // USER_ACCOUNT_LOCKED before any credential check so a correct credential
    // can't bypass the lock and OTP-issuing flows don't burn a one-shot code
    // on a guaranteed-fail attempt. attempt_count >= max means a prior run of
    // HandleFailedLoginAttemptAsync (or an admin/test setting the counter
    // directly) already locked the account.
    // Returns a rejection when the account is attempt-locked, plus the User to
    // continue with — which may be an in-memory-unlocked copy when the cool-down
    // (LockoutCooldownSeconds) has elapsed since the last failed/blocked attempt.
    // The cool-down window is measured from LastFailedLogin and refreshed on every
    // blocked attempt (reset-on-every-attempt), so a persistent attacker never
    // auto-unlocks — only a genuinely idle account does. Applies ONLY to the
    // attempt-counter lock; a manually-deactivated / never-verified account
    // (attempt_count < max) is left to RejectIfNotActive and never auto-unlocks.
    private async Task<(Result<(string Access, string Refresh, User User)>? Rejection, User User)>
        RejectIfAttemptLockedAsync(User user, CancellationToken ct)
    {
        var maxAttempts = settings.Value.MaxFailedLoginAttempts;
        if (maxAttempts <= 0 || user.AttemptCount is not int count || count < maxAttempts)
            return (null, user); // not attempt-locked

        var cooldown = settings.Value.LockoutCooldownSeconds;
        if (cooldown > 0 && user.LastFailedLogin is DateTime lastFailed
            && (TimeUtils.Now() - lastFailed).TotalSeconds > cooldown)
        {
            // Cool-down elapsed → auto-unlock and let the normal credential check run.
            await users.UnlockAfterCooldownAsync(user.Shortname, ct);
            return (null, user with { AttemptCount = 0, IsActive = true, LastFailedLogin = null });
        }

        // Still locked → refresh the cool-down anchor (so ongoing attacks keep the
        // window from ever elapsing) and reject. Message is kept identical to the
        // fresh-lock path (generic — no remaining-time leak, no message drift).
        await users.TouchLastFailedLoginAsync(user.Shortname, TimeUtils.Now(), ct);
        return (Result<(string, string, User)>.Fail(
            InternalErrorCode.USER_ACCOUNT_LOCKED,
            "Account has been locked due to too many failed login attempts.",
            ErrorTypes.Auth), user);
    }

    // Public wrapper around the private failed-attempt counter so out-of-class
    // callers (currently only OtpHandler./password-reset-confirm) can apply the
    // same account-lockout discipline /user/login enforces on wrong OTPs.
    // Returns true when this attempt caused the account to lock.
    public Task<bool> RecordFailedAttemptAsync(User user, CancellationToken ct = default)
        => HandleFailedLoginAttemptAsync(user, ct);

    private async Task<bool> HandleFailedLoginAttemptAsync(User user, CancellationToken ct)
    {
        await users.IncrementAttemptAsync(user.Shortname, TimeUtils.Now(), ct);

        var maxAttempts = settings.Value.MaxFailedLoginAttempts;
        if (maxAttempts <= 0) return false;

        // Load fresh — the attempt count in `user` is pre-increment and stale.
        var refreshed = await users.GetByShortnameAsync(user.Shortname, ct);
        if (refreshed is null) return false;
        if (refreshed.AttemptCount is not int count || count < maxAttempts) return false;
        if (!refreshed.IsActive) return true; // already locked by a prior attempt

        var locked = refreshed with { IsActive = false, UpdatedAt = TimeUtils.Now() };
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
    // through the same code path as password/OTP login. Keeping it
    // internal localizes exposure to the assembly while allowing reuse.
    internal async Task<Result<(string Access, string Refresh, User User)>> ProcessLoginAsync(
        User user, UserLoginRequest req,
        Dictionary<string, string>? requestHeaders, CancellationToken ct)
    {
        await users.ResetAttemptsAsync(user.Shortname, ct);

        // Python parity: bot users are completely outside the session-inactivity
        // machinery (utils/jwt.py:78,114 short-circuit set_user_session and
        // get_user_session for them). They neither populate nor consume entries
        // in MaxSessionsPerUser. Without this guard, a CI/MCP bot logging in
        // would churn the eviction queue and silently kick out human sessions
        // — and the matching read-side bypass in JwtBearerSetup.OnTokenValidated
        // would still reject the bot's token on its next request.
        var isBot = user.Type == Dmart.Models.Enums.UserType.Bot;

        // Python: max_sessions_per_user enforcement — check session count before
        // creating a new one. If at capacity, the oldest session should be evicted
        // or the login should fail. Python's get_user_session checks the count;
        // we limit by deleting oldest sessions when over the limit.
        var maxSessions = settings.Value.MaxSessionsPerUser;
        if (!isBot && maxSessions > 0)
        {
            // Evict excess sessions (keep newest maxSessions-1 to make room for the new one)
            await users.EvictExcessSessionsAsync(user.Shortname, maxSessions - 1, ct);
        }

        // Sync the in-memory copy with ResetAttemptsAsync so callers see the
        // post-login counter even though we don't replay the full row to PG.
        var updatedUser = user with { AttemptCount = 0, LastFailedLogin = null };
        string? newDeviceId = null;
        if (!string.IsNullOrEmpty(req.DeviceId) && req.DeviceId != user.DeviceId)
        {
            newDeviceId = req.DeviceId;
            updatedUser = updatedUser with { DeviceId = req.DeviceId };
        }

        // Python tracks last_login = {timestamp, headers} on every successful login.
        Dictionary<string, object>? loginInfo = null;
        if (requestHeaders is not null)
        {
            loginInfo = new Dictionary<string, object>
            {
                // Cast to int so source-gen JSON can serialize it (no Int64 TypeInfo).
                ["timestamp"] = (int)DateTimeOffset.Now.ToUnixTimeSeconds(),
                ["headers"] = requestHeaders,
            };
            updatedUser = updatedUser with { LastLogin = loginInfo };
        }

        // Targeted UPDATE rather than UpsertAsync: a plugin's after-hook
        // (e.g. OAuth → update_user attaching a Payload via
        // UpsertWithPriorAsync) may have written between the auth check
        // and this point; replaying the pre-login in-memory row would
        // erase those changes.
        if (newDeviceId is not null || loginInfo is not null)
        {
            await users.TouchLoginAsync(user.Shortname, newDeviceId, loginInfo, ct);
            updatedUser = updatedUser with { UpdatedAt = TimeUtils.Now() };
        }

        var access = jwt.IssueAccess(updatedUser.Shortname, updatedUser.Roles, updatedUser.Type);
        var refresh = jwt.IssueRefresh(updatedUser.Shortname, updatedUser.Type);

        // Create session row (Python: db.set_user_session). If the client
        // supplied a firebase_token on the login body, persist it on the
        // session row so a future push plugin can discover it via
        // UserRepository.GetSessionFirebaseTokensAsync. Python parity.
        // Skip for bots — utils/jwt.py:114 in Python doesn't create a row
        // for them at all (matching the bypass on the read side).
        if (!isBot)
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
                InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user missing", ErrorTypes.Db);

        // Reject patches targeting protected payload-body fields. Python
        // (api/user/router.py:623-633) walks `attributes.payload.body.<field>`
        // against settings.user_profile_payload_protected_fields — it is
        // payload content that's restricted, not top-level Record attributes.
        var protectedCsv = settings.Value.UserProfilePayloadProtectedFields;
        // Screen the SAME body shapes PayloadMerge would later merge (JsonElement or
        // Dictionary), so a protected field can't slip in via a form the check missed.
        if (!string.IsNullOrWhiteSpace(protectedCsv)
            && PayloadMerge.ExtractBody(patch.GetValueOrDefault("payload"))
                is { ValueKind: JsonValueKind.Object } bodyProtEl)
        {
            var protectedFields = protectedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var prop in bodyProtEl.EnumerateObject())
            {
                if (protectedFields.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    return Result<User>.Fail(
                        InternalErrorCode.PROTECTED_FIELD,
                        "Attempt to update a protected field", ErrorTypes.Restriction);
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
                    InternalErrorCode.INVALID_PASSWORD_RULES, "Invalid username or password", ErrorTypes.JwtAuth);
            // Python parity (api/user/router.py:665-682). old_password is
            // only required when ALL THREE hold:
            //   * client is setting a new password (guaranteed by the outer if)
            //   * the user already has a password in the DB (user.Password
            //     non-empty) — a user who never set one can freely set their
            //     first without prior-secret knowledge
            //   * force_password_change is NOT set — an admin-reset user
            //     mid-flow bypasses the old-password check so they can pick
            //     a fresh password without proving the old one.
            //     A side-effect of this gate: force_password_change=true users
            //     can't trip the lockout counter via /user/profile because
            //     the wrong-old_password branch never runs for them.
            if (!string.IsNullOrEmpty(user.Password) && !user.ForcePasswordChange)
            {
                //   * missing old_password   → 403 PASSWORD_RESET_ERROR, type=auth,
                //     message "Wrong password have been provided!"
                //   * old_password mismatch  → 401 UNMATCHED_DATA, type=request,
                //     message "mismatch with the information provided"
                if (!patch.TryGetValue("old_password", out var oldPwObj) || oldPwObj is null)
                    return Result<User>.Fail(
                        InternalErrorCode.PASSWORD_RESET_ERROR,
                        "Wrong password have been provided!", ErrorTypes.Auth);
                if (!hasher.Verify(oldPwObj.ToString()!, user.Password))
                {
                    // Wrong old_password counts toward the lockout threshold.
                    // Without this, an attacker who hijacks a session can brute
                    // the original password indefinitely on the change-password
                    // path while never tripping the login-side counter.
                    var locked = await HandleFailedLoginAttemptAsync(user, ct);
                    if (locked)
                        return Result<User>.Fail(
                            InternalErrorCode.USER_ACCOUNT_LOCKED,
                            "Account has been locked due to too many failed login attempts.", ErrorTypes.Auth);
                    return Result<User>.Fail(
                        InternalErrorCode.UNMATCHED_DATA,
                        "mismatch with the information provided", ErrorTypes.Request);
                }
            }
            newPasswordHash = string.IsNullOrEmpty(newPw) ? null : hasher.Hash(newPw!);
        }

        // Str only accepts scalar strings. When a client sends a non-string
        // (object, array, number, bool) for a field declared as a string —
        // e.g. {"email": {"foo":"bar"}} — preserve the existing value instead
        // of stuffing a JSON literal into the column. Matches Python's
        // Pydantic string-field validation outcome: bad type → don't apply.
        static string? Str(Dictionary<string, object> d, string k, string? fallback)
        {
            if (!d.TryGetValue(k, out var v) || v is null) return fallback;
            if (v is string s) return s;
            if (v is JsonElement el)
                return el.ValueKind == JsonValueKind.String ? el.GetString() : fallback;
            return v.ToString() ?? fallback;
        }

        // force_password_change is NOT self-settable via /user/profile — Python
        // intentionally comments out the patch assignment (api/user/router.py:683-684).
        // A user could otherwise clear an admin-mandated reset on themselves. The flag
        // is only auto-cleared when they actually change their password (it's
        // meaningless once they've picked one); otherwise it carries through unchanged.
        var resolvedForcePasswordChange = newPasswordHash is not null ? false : user.ForcePasswordChange;

        // Email change: Python parity (router.py:688-739).
        //   * patched email != stored email  → email_otp REQUIRED
        //   * email_otp must match users:otp:otps/<email> (peek, not consume —
        //     matches Python's verify_user which calls db.get_otp)
        //   * new email must not collide with another user (validate_uniqueness)
        //   * on success: lowercase, replace, flip is_email_verified=true
        // When the posted email is the same as the stored value, we silently
        // no-op (matches Python: the `!=` guard short-circuits the OTP check).
        string? resolvedEmail = user.Email;
        bool resolvedIsEmailVerified = user.IsEmailVerified;
        var rawEmail = Str(patch, "email", null);
        if (!string.IsNullOrEmpty(rawEmail))
        {
            var newEmail = rawEmail.ToLowerInvariant();
            if (!string.Equals(newEmail, user.Email, StringComparison.Ordinal))
            {
                var emailOtp = Str(patch, "email_otp", null);
                if (string.IsNullOrEmpty(emailOtp))
                    return Result<User>.Fail(InternalErrorCode.SESSION,
                        "Email OTP is required to update your email", ErrorTypes.Create);
                var storedOtp = await otp.GetCodeAsync(newEmail, ct);
                if (storedOtp is null || storedOtp != emailOtp)
                    return Result<User>.Fail(InternalErrorCode.SESSION,
                        "Invalid Email OTP", ErrorTypes.Create);
                var collision = await users.GetByEmailAsync(newEmail, ct);
                if (collision is not null && !string.Equals(collision.Shortname, user.Shortname, StringComparison.Ordinal))
                    return Result<User>.Fail(InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                        $"Entry properties should be unique: @email:{newEmail} ", ErrorTypes.Request);
                resolvedEmail = newEmail;
                resolvedIsEmailVerified = true;
            }
        }

        // Msisdn change: Python parity (router.py:699-754). Same gating as email.
        string? resolvedMsisdn = user.Msisdn;
        bool resolvedIsMsisdnVerified = user.IsMsisdnVerified;
        var newMsisdn = Str(patch, "msisdn", null);
        if (!string.IsNullOrEmpty(newMsisdn) && !string.Equals(newMsisdn, user.Msisdn, StringComparison.Ordinal))
        {
            var msisdnOtp = Str(patch, "msisdn_otp", null);
            if (string.IsNullOrEmpty(msisdnOtp))
                return Result<User>.Fail(InternalErrorCode.SESSION,
                    "MSISDN OTP is required to update your msisdn", ErrorTypes.Create);
            var storedOtp = await otp.GetCodeAsync(newMsisdn, ct);
            if (storedOtp is null || storedOtp != msisdnOtp)
                return Result<User>.Fail(InternalErrorCode.SESSION,
                    "Invalid MSISDN OTP", ErrorTypes.Create);
            var collision = await users.GetByMsisdnAsync(newMsisdn, ct);
            if (collision is not null && !string.Equals(collision.Shortname, user.Shortname, StringComparison.Ordinal))
                return Result<User>.Fail(InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                    $"Entry properties should be unique: @msisdn:{newMsisdn} ", ErrorTypes.Request);
            resolvedMsisdn = newMsisdn;
            resolvedIsMsisdnVerified = true;
        }

        // Python parity: deep-merge patch.payload.body into user.payload.body
        // (creating a default Payload if the user had none), via the shared
        // PayloadMerge used by the managed-user and entry update paths. Schema
        // validation is intentionally omitted — UserService has no SchemaService
        // dependency and Python only runs it for schema-bearing payloads.
        var resolvedPayload = PayloadMerge.MergeBody(user.Payload, patch.GetValueOrDefault("payload"));

        // Roles and Groups are intentionally absent from this `with` block: a
        // user can never change their own access via self-service
        // /user/profile, so any `roles`/`groups` in the patch body is ignored
        // and the stored values carry through unchanged. Mirrors the create
        // path, which also refuses client-supplied roles/groups.
        var updated = user with
        {
            Email = resolvedEmail,
            Msisdn = resolvedMsisdn,
            IsEmailVerified = resolvedIsEmailVerified,
            IsMsisdnVerified = resolvedIsMsisdnVerified,
            Language = patch.TryGetValue("language", out var l) && l is not null
                ? ParseLanguage(l.ToString())
                : user.Language,
            Displayname = patch.TryGetValue("displayname", out var dn) && dn is not null
                ? ParseTranslation(dn) ?? user.Displayname
                : user.Displayname,
            Description = patch.TryGetValue("description", out var desc) && desc is not null
                ? ParseTranslation(desc) ?? user.Description
                : user.Description,
            DeviceId = Str(patch, "device_id", user.DeviceId),
            ForcePasswordChange = resolvedForcePasswordChange,
            Password = newPasswordHash ?? user.Password,
            // Python's set_user_profile calls db.clear_failed_password_attempts
            // after hashing a new password — a user who just reset their own
            // password shouldn't be one mistyped login away from being locked.
            AttemptCount = newPasswordHash is not null ? 0 : user.AttemptCount,
            Payload = resolvedPayload,
            UpdatedAt = TimeUtils.Now(),
        };
        await users.UpsertAsync(updated, ct);

        // Python: if password changed and logout_on_pwd_change, delete all sessions.
        if (newPasswordHash is not null && settings.Value.LogoutOnPwdChange)
            await users.DeleteAllSessionsAsync(shortname, ct);

        // Python: if is_active set to false, delete all sessions.
        if (patch.TryGetValue("is_active", out var ia) && ia is false)
            await users.DeleteAllSessionsAsync(shortname, ct);

        // Python parity: store_entry_diff — record what changed so
        // /managed/query?type=history surfaces the audit trail for self-service
        // profile updates the same way it does for entry updates. Runs AFTER
        // the session-cleanup branches above so a transient history-write
        // failure can't leave logout_on_pwd_change un-applied. The diff
        // intentionally omits secrets and bookkeeping (password hash, attempt
        // counter, updated_at noise); password changes still appear via the
        // boolean force_password_change flip when relevant. Actor == target
        // shortname is sound for the self-service /user/profile path; an admin
        // path that updates someone else's profile must thread its own actor.
        var historyDiff = HistoryDiffUtil.ComputeUserDiff(user, updated);
        if (historyDiff.Count > 0)
            await history.AppendAsync(MgmtSpace, "/users", shortname, shortname, null,
                historyDiff, ct);

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

        // After-hook fires only when something actually changed — symmetric
        // with the history.AppendAsync guard above, so a no-op patch produces
        // neither a history row nor a plugin event. The payload carries the
        // same {field_path: {old, new}} diff persisted to history; plugins
        // (e.g. action_log) read field-level deltas from there. Mirrors
        // EntryService.UpdateAsync:372-374 — single source of truth.
        if (historyDiff.Count > 0)
        {
            var afterEvent = new Event
            {
                SpaceName = MgmtSpace,
                Subpath = "/users",
                Shortname = updated.Shortname,
                ActionType = ActionType.Update,
                ResourceType = ResourceType.User,
                UserShortname = shortname,
            };
            afterEvent.Attributes["history_diff"] = historyDiff;
            await plugins.AfterActionAsync(afterEvent, ct);
        }

        return Result<User>.Ok(updated);
    }

    public async Task DeleteAsync(string shortname, CancellationToken ct = default)
    {
        // Python: remove all sessions before deleting user.
        await users.DeleteAllSessionsAsync(shortname, ct);
        await users.DeleteAsync(shortname, ct);
    }

    public async Task LogoutAsync(string? shortname, string? token, CancellationToken ct = default)
    {
        // Python: db.remove_user_session() — delete the specific session row.
        // The repository hashes the raw JWT and looks up the row by
        // (shortname, hashed_token); both inputs must be non-empty for the
        // lookup to identify a single row, so a missing actor or token is a
        // silent no-op rather than a wildcard delete.
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(shortname))
        {
            await users.DeleteSessionAsync(shortname, token, ct);
            return;
        }
        // Surface the silent-skip path so an expired-cookie /user/logout
        // (where the JWT principal didn't validate) doesn't disappear from
        // the trace. Token-without-actor shouldn't happen in normal flow —
        // JwtBearer would have rejected the call before we got here.
        log.LogInformation(
            "logout: no-op (actor={ActorPresent}, token={TokenPresent})",
            !string.IsNullOrEmpty(shortname),
            !string.IsNullOrEmpty(token));
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
