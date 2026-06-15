using Dmart.Auth;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// dmart's `otp` table uses HSTORE for the `value` column (not JSONB).
// HSTORE is a key→string map; we store the code, the destination, and an expires_at
// ISO timestamp so the application layer can enforce TTL.
//
// The `code` field is never the raw 6-digit OTP — it's a keyed HMAC (OtpHasher),
// so a DB read can't surface a live, replayable credential within its TTL. The
// hash is deterministic, so verification stays a single SELECT + fixed-time
// compare (no per-row KDF on the auth hot path).
public sealed class OtpRepository(Db db, OtpHasher hasher)
{
    public async Task StoreAsync(string key, string code, DateTime expiresAt, CancellationToken ct = default)
    {
        var hstore = new Dictionary<string, string?>
        {
            ["code"] = hasher.Hash(code),
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

    // Seconds elapsed since the OTP row at `key` was last written. Null when
    // no row exists. Mirrors Python's `otp_created_since` — used by
    // /user/otp-request to enforce the resend cooldown.
    public async Task<int?> GetCreatedSinceAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT EXTRACT(EPOCH FROM (NOW() - timestamp))::int FROM otp WHERE key = $1", conn);
        cmd.Parameters.Add(new() { Value = key });
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw is null || raw is DBNull) return null;
        return Convert.ToInt32(raw);
    }

    // Peek-verify: true when a non-expired OTP at `key` hashes to the same value
    // as `candidate`, WITHOUT consuming it (Python parity: verify_user calls
    // db.get_otp, which doesn't delete). Used by /user/create and the
    // /user/profile email/msisdn change so a failed attempt leaves the OTP usable
    // for another try within its TTL. Because codes are stored hashed, callers
    // can no longer fetch the plaintext to compare — they hand us the candidate
    // and we compare hashes here.
    public async Task<bool> VerifyPeekAsync(string key, string candidate, CancellationToken ct = default)
    {
        var stored = await PeekStoredHashAsync(key, ct);
        if (stored is null) return false;
        var expected = hasher.Hash(candidate);
        // Fixed-time compare over the hex hashes (both fixed 64-char ASCII, so
        // the length precondition always holds). The keyed hash already strips
        // any per-digit timing signal; this keeps the compare uniform.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(stored),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }

    // Returns the stored (hashed) OTP value at `key`, or null when no row exists
    // or it has expired. This is the keyed HMAC, NOT a usable code — exposed for
    // existence/freshness assertions and never compared against a plaintext code.
    // Callers validating a code use VerifyPeekAsync; this only answers "is there
    // a live OTP here?" (and, since the hash is deterministic, "is it unchanged?").
    public async Task<string?> PeekStoredHashAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT value FROM otp WHERE key = $1", conn);
        cmd.Parameters.Add(new() { Value = key });
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw is not IDictionary<string, string?> dict) return null;
        if (!dict.TryGetValue("code", out var code)) return null;
        if (dict.TryGetValue("expires_at", out var expRaw)
            && DateTime.TryParse(expRaw, out var exp) && exp < TimeUtils.Now()) return null;
        return code;
    }

    // Unconditional delete of the OTP row at `key`. Used by /user/create to
    // consume the registration OTP once the account is persisted, so a stored
    // code can't be replayed (e.g. via /user/otp-confirm) after the user
    // exists. A no-op when no row is present.
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM otp WHERE key = $1", conn);
        cmd.Parameters.Add(new() { Value = key });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<bool> VerifyAndConsumeAsync(string key, string code, CancellationToken ct = default)
        => VerifyAndConsumeAsync(key, code, maxAttempts: 0, ct);

    // maxAttempts > 0 caps wrong guesses against a single stored code: each
    // mismatch bumps an "attempts" counter in the HSTORE value, and once it
    // reaches the cap the row is deleted so the code can never be redeemed —
    // even by a later correct guess. This closes the brute-force window on
    // anonymous OTP verification that per-IP rate limiting alone can't (a
    // distributed attacker spreads guesses across IPs). maxAttempts == 0
    // preserves the original uncapped behavior.
    //
    // Deliberate server-side divergence from Python dmart; the wire response is
    // unchanged (an exhausted code looks identical to an expired one).
    public async Task<bool> VerifyAndConsumeAsync(
        string key, string code, int maxAttempts, CancellationToken ct = default)
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
                if (!dict.TryGetValue("code", out var stored) || stored is null) return false;
                if (dict.TryGetValue("expires_at", out var expRaw)
                    && DateTime.TryParse(expRaw, out var exp) && exp < TimeUtils.Now()) return false;
                // `stored` is the keyed HMAC of the real code, never the plaintext;
                // hash the supplied guess the same way and compare in fixed time.
                // Both sides are fixed-width hex, so the length check is constant.
                var storedBytes = System.Text.Encoding.UTF8.GetBytes(stored);
                var inputBytes = System.Text.Encoding.UTF8.GetBytes(hasher.Hash(code));
                var matches = storedBytes.Length == inputBytes.Length
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedBytes, inputBytes);
                if (!matches)
                {
                    await RecordFailedAttemptAsync(conn, tx, key, dict, maxAttempts, ct);
                    await tx.CommitAsync(ct);
                    return false;
                }
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

    // On a wrong guess, either bump the attempts counter or, once the cap is
    // reached, delete the row so the code is permanently spent. No-op when
    // capping is disabled (maxAttempts <= 0).
    private static async Task RecordFailedAttemptAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key,
        IDictionary<string, string?> dict, int maxAttempts, CancellationToken ct)
    {
        if (maxAttempts <= 0) return;

        var attempts = dict.TryGetValue("attempts", out var a)
            && int.TryParse(a, out var n) ? n + 1 : 1;

        if (attempts >= maxAttempts)
        {
            await using var del = new NpgsqlCommand("DELETE FROM otp WHERE key = $1", conn, tx);
            del.Parameters.Add(new() { Value = key });
            await del.ExecuteNonQueryAsync(ct);
            return;
        }

        // hstore || hstore('attempts', $2) merges/overwrites just the one key,
        // leaving code/expires_at intact.
        await using var upd = new NpgsqlCommand(
            "UPDATE otp SET value = value || hstore('attempts', $2) WHERE key = $1", conn, tx);
        upd.Parameters.Add(new() { Value = key });
        upd.Parameters.Add(new() { Value = attempts.ToString() });
        await upd.ExecuteNonQueryAsync(ct);
    }
}
