namespace Dmart.Config;

public sealed class DmartSettings
{
    public string SpacesRoot { get; set; } = "./spaces";
    public string JwtSecret { get; set; } = "change-me";
    public string JwtIssuer { get; set; } = "dmart";
    public string JwtAudience { get; set; } = "dmart";
    public int JwtAccessMinutes { get; set; } = 15;
    public int JwtRefreshDays { get; set; } = 30;
    public string RedisConnection { get; set; } = "localhost:6379";
    public string? PostgresConnection { get; set; }
    public string DefaultLanguage { get; set; } = "en";
    public bool EnableSqlBackend { get; set; }

    // First-run admin bootstrap. If AdminShortname is set and the user doesn't exist,
    // a super_admin role + admin user are created on startup.
    public string? AdminShortname { get; set; }
    public string? AdminPassword { get; set; }
    public string? AdminEmail { get; set; }

    // count_history snapshot cadence (in minutes). Set to a large value to disable
    // periodic recording — the snapshotter still writes one row on startup.
    public int CountHistoryIntervalMinutes { get; set; } = 360;

    // Optional websocket bridge URL. When set, the realtime_updates_notifier plugin
    // posts broadcast messages to {WebsocketUrl}/broadcast-to-channels after CRUD
    // events. Leave null to disable realtime broadcasting without unloading the
    // plugin.
    public string? WebsocketUrl { get; set; }
}
