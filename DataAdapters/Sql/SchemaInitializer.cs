using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Runs once on startup; creates tables if they don't exist. Idempotent.
public sealed class SchemaInitializer(Db db, ILogger<SchemaInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!db.IsConfigured)
        {
            log.LogInformation("database not configured — skipping schema initialization");
            return;
        }
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(SqlSchema.CreateAll, conn);
            await cmd.ExecuteNonQueryAsync(ct);
            log.LogInformation("dmart schema ready");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "schema initialization failed — continuing without DB");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
