using System.Security.Cryptography;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// dmart's URL shortener table is `urlshorts` (SQLAlchemy lowercased class name).
// The `timestamp` column holds the link's EXPIRY (local wall-clock, matching
// the rest of dmart's timezone-less storage); ResolveAsync refuses anything
// past it. Tokens are 128-bit CSPRNG so they can't be brute-force enumerated.
public sealed class LinkRepository(Db db)
{
    // 16 random bytes → 32 lowercase hex chars (128 bits). Replaces the old
    // 8–10 hex-char (32–40 bit) tokens, which were small enough to enumerate.
    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public async Task<string> CreateAsync(string url, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var token = NewToken();
        var expiresAt = TimeUtils.Now().Add(ttl ?? TimeSpan.FromHours(24));
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO urlshorts (uuid, token_uuid, url, timestamp)
            VALUES (gen_random_uuid(), $1, $2, $3)
            """, conn);
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = url });
        cmd.Parameters.Add(new() { Value = expiresAt });
        await cmd.ExecuteNonQueryAsync(ct);
        return token;
    }

    public async Task CreateWithTokenAsync(string token, string url, DateTime expiresAt, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO urlshorts (uuid, token_uuid, url, timestamp)
            VALUES (gen_random_uuid(), $1, $2, $3)
            ON CONFLICT (token_uuid) DO UPDATE SET url = EXCLUDED.url, timestamp = EXCLUDED.timestamp
            """, conn);
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = url });
        cmd.Parameters.Add(new() { Value = expiresAt });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> ResolveAsync(string token, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // Compare against a C# wall-clock value (naive, the same basis the
        // expiry was written with) rather than SQL NOW() (timestamptz) to avoid
        // a session-timezone-dependent cast on the naive `timestamp` column.
        await using var cmd = new NpgsqlCommand(
            "SELECT url FROM urlshorts WHERE token_uuid = $1 AND timestamp > $2", conn);
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = TimeUtils.Now() });
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
