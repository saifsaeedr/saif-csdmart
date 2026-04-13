using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// dmart's `otp` table uses HSTORE for the `value` column (not JSONB).
// HSTORE is a key→string map; we store the code, the destination, and an expires_at
// ISO timestamp so the application layer can enforce TTL.
public sealed class OtpRepository(Db db)
{
    public async Task StoreAsync(string key, string code, DateTime expiresAt, CancellationToken ct = default)
    {
        var hstore = new Dictionary<string, string?>
        {
            ["code"] = code,
            ["expires_at"] = expiresAt.ToString("O"),
        };
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO otp (key, value, timestamp)
            VALUES ($1, $2, NOW())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, timestamp = NOW()
            """, conn);
        cmd.Parameters.Add(new() { Value = key });
        cmd.Parameters.Add(new() { Value = hstore, NpgsqlDbType = NpgsqlDbType.Hstore });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> VerifyAndConsumeAsync(string key, string code, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = new NpgsqlCommand("SELECT value FROM otp WHERE key = $1", conn, tx))
            {
                cmd.Parameters.Add(new() { Value = key });
                var raw = await cmd.ExecuteScalarAsync(ct);
                if (raw is not IDictionary<string, string?> dict) return false;
                if (!dict.TryGetValue("code", out var stored) || stored != code) return false;
                if (dict.TryGetValue("expires_at", out var expRaw)
                    && DateTime.TryParse(expRaw, out var exp) && exp < DateTime.UtcNow) return false;
            }
            await using var del = new NpgsqlCommand("DELETE FROM otp WHERE key = $1", conn, tx);
            del.Parameters.Add(new() { Value = key });
            await del.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
