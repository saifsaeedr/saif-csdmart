using Npgsql;

namespace Dmart.SqlAdapter;

// Connection factory for a dmart-managed Postgres database.
//
// Mirrors DataAdapters/Sql/Db.cs from the dmart server but is fully standalone
// — no IOptions<DmartSettings> dependency so the module drops cleanly into any
// ASP.NET host. Construction takes either a raw connection string (preferred
// when you want full control) or the same DATABASE_* knobs dmart Python's
// config.env exposes, so a plain Python-style config can drive it unchanged.
//
// Opens a fresh NpgsqlConnection per call; Npgsql does pooling under the hood.
public sealed class DmartDb
{
    private readonly string _connectionString;

    public DmartDb(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public DmartDb(DmartDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.ResolveConnectionString();
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var c = new NpgsqlConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    // Transactional helper with bounded retry on PG 40P01 (deadlock detected).
    // Deadlock losers get rolled back fully, so replaying from scratch is
    // correct. Use from any caller doing a multi-statement transaction where
    // concurrent writers can race on row locks. Anything other than 40P01
    // bubbles up unchanged.
    public async Task<T> ExecuteWithRetryOnDeadlockAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == "40P01" && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
        }
    }
}

// Same shape as DmartSettings's DATABASE_* fields. Use it when you don't want
// to hand-assemble an Npgsql connection string, e.g. when reading values out
// of IConfiguration / appsettings.json.
public sealed class DmartDbOptions
{
    public string? ConnectionString { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Username { get; set; } = "dmart";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "dmart";
    public int PoolSize { get; set; } = 20;
    public int MaxOverflow { get; set; } = 10;
    public int PoolTimeout { get; set; } = 30;
    public int PoolRecycle { get; set; } = 300;

    public string ResolveConnectionString()
    {
        if (!string.IsNullOrEmpty(ConnectionString)) return ConnectionString!;
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            Database = Database,
            MaxPoolSize = PoolSize + MaxOverflow,
            Timeout = PoolTimeout,
            ConnectionIdleLifetime = PoolRecycle,
        };
        return csb.ConnectionString;
    }
}
