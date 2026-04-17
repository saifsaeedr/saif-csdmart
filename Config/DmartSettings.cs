namespace Dmart.Config;

// Mirrors dmart/backend/utils/settings.py::Settings where the fields actually
// drive port behavior. Any field added here is expected to be wired into the
// code somewhere — we don't mirror Python fields that have no C# consumer yet.
//
// Config sources, in override order (later wins):
//   1. config.env ($BACKEND_ENV → ./config.env → ~/.dmart/config.env)
//   2. Environment variables (Dmart__Xxx)
public sealed class DmartSettings
{
    public string SpacesRoot { get; set; } = "./spaces";
    public string JwtSecret { get; set; } = "change-me-change-me-change-me-32b";
    public string JwtIssuer { get; set; } = "dmart";
    public string JwtAudience { get; set; } = "dmart";
    public int JwtAccessMinutes { get; set; } = 15;
    public int JwtRefreshDays { get; set; } = 30;

    // Full Npgsql connection string. If unset, Db builds one from the
    // individual DATABASE_* components below (matching Python's behavior
    // of assembling a SQLAlchemy URL out of username/password/host/port/name).
    public string? PostgresConnection { get; set; }

    // dmart Python's individual database components. Used as a fallback when
    // PostgresConnection isn't explicitly set — see Db.cs for the assembly
    // logic. Keeping the field names aligned with Python dotenv keys so
    // config.env files drop in unchanged.
    public string DatabaseHost { get; set; } = "localhost";
    public int DatabasePort { get; set; } = 5432;
    public string DatabaseUsername { get; set; } = "dmart";
    public string DatabasePassword { get; set; } = "";
    public string DatabaseName { get; set; } = "dmart";
    public int DatabasePoolSize { get; set; } = 10;
    public int DatabaseMaxOverflow { get; set; } = 10;
    public int DatabasePoolTimeout { get; set; } = 30;
    public int DatabasePoolRecycle { get; set; } = 1800;

    // ADMIN_PASSWORD and ADMIN_EMAIL are read ONLY on first startup, by
    // AdminBootstrap, when the hardcoded `dmart` admin user doesn't already
    // exist. Subsequent restarts ignore these values — use `dmart
    // set_password` / the /user/profile endpoint to change them afterwards.
    // Main intended use: provisioning a known admin in CI / fresh installs.
    // Leaving AdminPassword unset creates a passwordless admin account
    // (common for interactive setup where `dmart set_password` follows).
    public string? AdminPassword { get; set; }
    public string? AdminEmail { get; set; }

    // count_history snapshot cadence (in minutes). Set to a large value to
    // disable periodic recording — the snapshotter still writes one row on
    // startup.
    public int CountHistoryIntervalMinutes { get; set; } = 360;

    // Python-parity runtime knobs. Defaults mirror utils/settings.py so a
    // deployment config.env carrying only the DATABASE_* block still lands
    // on the same behavior as Python.
    public string AppUrl { get; set; } = "";
    public string ListeningHost { get; set; } = "0.0.0.0";
    public int ListeningPort { get; set; } = 8282;
    public string ManagementSpace { get; set; } = "management";
    public int MaxSessionsPerUser { get; set; } = 5;
    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int MaxQueryLimit { get; set; } = 10000;
    public int UrlShorterExpires { get; set; } = 60 * 60;
    public bool IsRegistrable { get; set; } = true;

    // When true, POST /user/create requires a valid email_otp / msisdn_otp
    // attribute that was previously obtained via /user/otp-request. When
    // false, registration proceeds without OTP verification. Mirrors
    // Python's `is_otp_for_create_required`.
    public bool IsOtpForCreateRequired { get; set; } = true;

    // Global TTL (seconds) for one-time passwords. OtpRepository enforces
    // this when verifying a code — entries older than OtpTokenTtl seconds
    // are treated as expired regardless of the per-endpoint "expires" value
    // that was stored at creation time. Mirrors Python's `otp_token_ttl`.
    public int OtpTokenTtl { get; set; } = 60 * 5;

    // If > 0, sessions that haven't been touched for this many seconds are
    // rejected at JWT validation time (and the session row is deleted). 0
    // disables the check. Mirrors Python's `session_inactivity_ttl`.
    public int SessionInactivityTtl { get; set; }

    // How long (seconds) a PUT /managed/lock stays held before any user can
    // take it. Prevents orphaned locks when a client crashes without calling
    // DELETE /managed/lock. Mirrors Python's `lock_period`.
    public int LockPeriod { get; set; } = 300;

    public string MockOtpCode { get; set; } = "123456";
    public bool MockSmtpApi { get; set; }
    public bool MockSmppApi { get; set; }
    public bool LogoutOnPwdChange { get; set; } = true;
    public int RequestTimeout { get; set; } = 35;

    // Logging format: "text" (default console) or "json" (structured JSON lines).
    // Python uses pythonjsonlogger → .ljson.log files. Set to "json" for
    // production deployments where logs are consumed by ELK/Loki/CloudWatch.
    public string LogFormat { get; set; } = "text";
    // Optional log file path. Empty = stdout only (container-friendly).
    // Python default: "../logs/dmart.ljson.log"
    public string LogFile { get; set; } = "";
    // Log level: trace, debug, information, warning, error, critical, none.
    // Default "information" matches Python. Set to "warning" in production
    // to reduce noise.
    public string LogLevel { get; set; } = "information";

    // CSV list of "space.schema" pairs allowed for public /submit endpoints.
    // Empty means allow all. Python: allowed_submit_models.
    public string AllowedSubmitModels { get; set; } = "";

    // CSV list of payload field names users can't update via POST /user/profile.
    // Python: user_profile_payload_protected_fields.
    public string UserProfilePayloadProtectedFields { get; set; } = "";

    // CORS allowlist — mirrors Python's utils/settings.py::allowed_cors_origins.
    // When non-empty, the response-headers middleware reflects matching Origin
    // values into Access-Control-Allow-Origin. When empty, the middleware
    // falls back to the same-host origin (http://{ListeningHost}:{ListeningPort})
    // so browsers don't see an open reflection.
    //
    // Stored as a CSV string so it binds cleanly from both dotenv
    // (ALLOWED_CORS_ORIGINS="http://a.com,http://b.com") and appsettings.json
    // ("AllowedCorsOrigins": "http://a.com,http://b.com"). The middleware
    // splits on commas at request time, trimming whitespace.
    // MUST be configured for production. Empty = same-host only fallback.
    public string AllowedCorsOrigins { get; set; } = "";

    // URL path prefix for the CXB frontend. Python default: "/cxb".
    // Change to "/" to serve CXB at the root, or "/admin" for a custom path.
    public string CxbUrl { get; set; } = "/cxb";

    // Helper — split the CSV into a trimmed string array. Done here (not in the
    // middleware) so tests can assert the parsing behavior directly.
    public string[] ParseAllowedCorsOrigins()
    {
        if (string.IsNullOrWhiteSpace(AllowedCorsOrigins)) return Array.Empty<string>();
        return AllowedCorsOrigins
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
