namespace Dmart.Config;

// Mirrors dmart/backend/utils/settings.py::Settings where the fields actually
// drive port behavior. Any field added here is expected to be wired into the
// code somewhere — we don't mirror Python fields that have no C# consumer yet.
//
// Config sources, in override order (later wins):
//   1. {BaseDir}/appsettings.json
//   2. config.env (loaded via DotEnv.Load() — Python-compatible lookup order)
//   3. Environment variables (Dmart__Xxx via the standard EnvironmentVariables
//      provider)
//   4. appsettings.{Environment}.json
// See Program.cs for the provider order.
public sealed class DmartSettings
{
    public string SpacesRoot { get; set; } = "./spaces";
    public string JwtSecret { get; set; } = "change-me";
    public string JwtIssuer { get; set; } = "dmart";
    public string JwtAudience { get; set; } = "dmart";
    public int JwtAccessMinutes { get; set; } = 15;
    public int JwtRefreshDays { get; set; } = 30;
    public string RedisConnection { get; set; } = "localhost:6379";

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

    public string DefaultLanguage { get; set; } = "en";
    public bool EnableSqlBackend { get; set; }

    // First-run admin bootstrap. If AdminShortname is set and the user doesn't
    // exist, a super_admin role + admin user are created on startup.
    public string? AdminShortname { get; set; }
    public string? AdminPassword { get; set; }
    public string? AdminEmail { get; set; }

    // count_history snapshot cadence (in minutes). Set to a large value to
    // disable periodic recording — the snapshotter still writes one row on
    // startup.
    public int CountHistoryIntervalMinutes { get; set; } = 360;

    // Optional websocket bridge URL. When set, the realtime_updates_notifier
    // plugin posts broadcast messages to {WebsocketUrl}/broadcast-to-channels
    // after CRUD events. Leave null to disable realtime broadcasting without
    // unloading the plugin.
    public string? WebsocketUrl { get; set; }

    // Python-parity runtime knobs. Defaults mirror utils/settings.py so a
    // deployment config.env carrying only the DATABASE_* block still lands
    // on the same behavior as Python.
    public string AppName { get; set; } = "dmart";
    public string AppUrl { get; set; } = "";
    public string ListeningHost { get; set; } = "0.0.0.0";
    public int ListeningPort { get; set; } = 8282;
    public string ManagementSpace { get; set; } = "management";
    public string UsersSubpath { get; set; } = "users";
    public int MaxSessionsPerUser { get; set; } = 5;
    public int LockPeriod { get; set; } = 300;
    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int MaxQueryLimit { get; set; } = 10000;
    public int OtpTokenTtl { get; set; } = 60 * 5;
    public int SessionInactivityTtl { get; set; }
    public int UrlShorterExpires { get; set; } = 60 * 60;
    public bool IsRegistrable { get; set; } = true;
    public bool IsOtpForCreateRequired { get; set; } = true;
    public string MockOtpCode { get; set; } = "123456";
    public bool MockSmtpApi { get; set; }
    public bool MockSmppApi { get; set; }

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
    public string AllowedCorsOrigins { get; set; } = "";

    // Helper — split the CSV into a trimmed string array. Done here (not in the
    // middleware) so tests can assert the parsing behavior directly.
    public string[] ParseAllowedCorsOrigins()
    {
        if (string.IsNullOrWhiteSpace(AllowedCorsOrigins)) return Array.Empty<string>();
        return AllowedCorsOrigins
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
