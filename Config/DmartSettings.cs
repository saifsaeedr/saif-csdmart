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
    public string JwtSecret { get; set; } = "change-me-change-me-change-me-32b";
    public string JwtIssuer { get; set; } = "dmart";
    public string JwtAudience { get; set; } = "dmart";
    // Python parity: backend/utils/settings.py sets jwt_access_expires =
    // 30 * 86400 (30 days) and issues only an access token at /user/login.
    // We match that default so Python-origin clients (tsdmart/cxb/catalog/
    // pydmart) don't 401 mid-session against C# dmart. The 2-token model
    // remains available for MCP OAuth clients via /oauth/token.
    public int JwtAccessMinutes { get; set; } = 60 * 24 * 30;
    public int JwtRefreshDays { get; set; } = 30;
    // Invitation tokens (minted via /user/reset or on user creation) live for
    // this many days. Python's jwt_access_expires default is 30 days for the
    // same payload, so we match.
    public int JwtInvitationDays { get; set; } = 30;

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

    // Minimum seconds between OTP re-sends for the same destination. Mirrors
    // Python's `allow_otp_resend_after`. /user/otp-request returns
    // OTP_RESEND_BLOCKED (HTTP 403) when a prior OTP was issued within this
    // window.
    public int AllowOtpResendAfter { get; set; } = 60;

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

    // ---- External SMS gateway (Python parity) ----
    // Base URL the CXB front-end uses to assemble invitation links that are
    // delivered via SMS/email. Python substitutes this into the template
    //   {invitation_link}/auth/invitation?invitation={token}&lang={lang}&user-type={type}
    // Leave empty to disable invitation-URL assembly (C# still returns the
    // raw JWT on the HTTP response when delivery is not configured).
    public string InvitationLink { get; set; } = "";

    // HTTP "auth-key" header value sent on every call to the SMS gateway
    // endpoints below. Mirrors Python's `smpp_auth_key` — gateway-specific.
    public string SmppAuthKey { get; set; } = "";

    // POST endpoint the OTP flow calls to deliver numeric OTP codes over SMS.
    // Python: `send_sms_otp_api`. Body is JSON {"msisdn":"...","text":"..."}.
    // Empty disables OTP SMS delivery — the code is then only retrievable
    // from server logs (dev) or via MOCK_OTP_CODE (when MOCK_SMPP_API=true).
    public string SendSmsOtpApi { get; set; } = "";

    // POST endpoint the general SMS flow calls (e.g. to deliver invitation
    // links to msisdn users). Python: `send_sms_api`. Same request shape as
    // `SendSmsOtpApi`. Empty disables outbound SMS for invitation delivery.
    public string SendSmsApi { get; set; } = "";

    // ---- Embeddings (for semantic search) ----
    // Semantic search is an opt-in feature. When both the pgvector extension
    // is installed in PostgreSQL AND EmbeddingApiUrl is set, dmart will embed
    // every entry it creates/updates and expose POST /managed/semantic-search
    // + the `dmart_semantic_search` MCP tool. When either is missing, those
    // features silently no-op and existing workloads are unaffected.
    //
    // Request shape sent to the endpoint (OpenAI-compatible):
    //   POST {EmbeddingApiUrl}  with Authorization: Bearer {EmbeddingApiKey}
    //   Body: { "model": "...", "input": "<text>" }
    //   Response: { "data": [ { "embedding": [floats...] } ], ... }
    // Works against OpenAI directly, Ollama's /api/embeddings with a
    // compatible wrapper, text-embeddings-inference, and most OpenAI-
    // compatible relays.
    public string EmbeddingApiUrl { get; set; } = "";
    public string EmbeddingApiKey { get; set; } = "";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    // ---- OAuth / social login (Python parity) ----
    // Each provider has its own client credentials + callback URL. Leaving a
    // provider's ClientId blank disables it — its endpoints return a clean
    // "provider not configured" error rather than attempting the outbound
    // call. See Api/User/OAuth/OAuthHandlers.cs for the endpoint surface.
    //
    // Google: `aud` on the id_token must match GoogleClientId. Used both by
    // the code-exchange flow (GET /user/google/callback) and the mobile
    // id-token flow (POST /user/google/mobile-login).
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    public string GoogleOauthCallback { get; set; } = "";

    // Facebook: debug_token verifies `app_id == FacebookClientId`. The
    // "app token" sent to graph.facebook.com/debug_token is the literal
    // concatenation "{ClientId}|{ClientSecret}" (not a separate OAuth token).
    public string FacebookClientId { get; set; } = "";
    public string FacebookClientSecret { get; set; } = "";
    public string FacebookOauthCallback { get; set; } = "";

    // Apple: id_token is a JWT signed with RS256 against one of the keys
    // published at https://appleid.apple.com/auth/keys. AppleClientId is the
    // bundle id (iOS) or service id (web). No client secret needed for the
    // mobile/id-token flow — web-callback code exchange requires a signed
    // "client assertion" JWT built from AppleTeamId + AppleKeyId +
    // AppleClientSecretPrivateKey (ES256); set those if you need the web
    // callback, otherwise they can stay blank and the mobile flow still works.
    public string AppleClientId { get; set; } = "";
    public string AppleOauthCallback { get; set; } = "";
    public string AppleTeamId { get; set; } = "";
    public string AppleKeyId { get; set; } = "";
    public string AppleClientSecretPrivateKey { get; set; } = "";

    public bool LogoutOnPwdChange { get; set; } = true;
    public int RequestTimeout { get; set; } = 35;

    // Timeout (seconds) for the `jq` subprocess invoked when a join sub-query
    // carries a jq_filter expression. Python default (backend/utils/settings.py).
    public int JqTimeout { get; set; } = 2;

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

    // URL path prefix for the Catalog frontend — second embedded SPA,
    // served by CatalogMiddleware. Default matches the <base href="/cat/">
    // in catalog/index.html. Share config.json lookup with CXB.
    public string CatUrl { get; set; } = "/cat";

    // ---- SMTP email gateway (Python parity) ----
    // Used by OtpProvider to deliver email OTP codes and by InvitationService
    // once email channels are enabled. When MailHost is empty the sender logs
    // a warning and falls back to the on-server log (same failure mode as the
    // SMS sender). MockSmtpApi short-circuits delivery for dev/CI.
    //
    // Python's dotenv keys: MAIL_HOST, MAIL_PORT, MAIL_USERNAME, MAIL_PASSWORD,
    // MAIL_FROM_ADDRESS, MAIL_FROM_NAME, MAIL_USE_TLS.
    public string MailHost { get; set; } = "";
    public int MailPort { get; set; } = 587;
    public string MailUsername { get; set; } = "";
    public string MailPassword { get; set; } = "";
    public string MailFromAddress { get; set; } = "noreply@admin.com";
    public string MailFromName { get; set; } = "";
    public bool MailUseTls { get; set; } = true;

    // Helper — split the CSV into a trimmed string array. Done here (not in the
    // middleware) so tests can assert the parsing behavior directly.
    public string[] ParseAllowedCorsOrigins()
    {
        if (string.IsNullOrWhiteSpace(AllowedCorsOrigins)) return Array.Empty<string>();
        return AllowedCorsOrigins
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
