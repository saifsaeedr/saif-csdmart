using System.Security.Cryptography;
using System.Text;
using Dmart.Config;

namespace Dmart.Auth;

// Deterministic, keyed hash for the `sessions.token` column. We don't want
// the column to hold the raw bearer JWT (a DB read should not yield
// replayable credentials), but we DO want the lookup to stay a single
// indexed equality predicate — `WHERE shortname = $1 AND token = $2` —
// because session validation runs on every authenticated request.
//
// HMAC-SHA256(key, token) gives us both:
//   * Without `key`, the column value can't be turned back into a usable
//     bearer token, even by an attacker who exfiltrates the entire DB.
//   * Hashing is deterministic, so a SELECT on the hash works as before
//     (no per-row Verify pass — that approach was prohibitively expensive
//     when paired with a password-grade KDF like Argon2).
//
// The key is derived from `JwtSecret` so operators don't need a second
// secret. Rotating JWT_SECRET implicitly invalidates all session rows
// (existing JWTs also stop validating, so this is the desired behavior).
// We mix in a domain-separation label so the derived key is distinct
// from any other use of the same secret material (e.g. the JWT-signing
// HMAC inside the JWT library).
//
// Output: lowercase hex (Convert.ToHexString().ToLowerInvariant()) — keeps
// the column ASCII, fixed-width (64 chars), and dump-safe.
public sealed class SessionTokenHasher
{
    private readonly byte[] _key;

    public SessionTokenHasher(DmartSettings settings)
    {
        var secret = settings.JwtSecret ?? "";
        // Domain-separated key: SHA256(secret || "dmart-session-token-v1").
        // Wrapping the secret through SHA256 also normalises any input length
        // to 32 bytes (HMAC-SHA256's internal block-size optimum), so callers
        // don't need to worry about JwtSecret being short or oversized.
        var bytes = new byte[Encoding.UTF8.GetByteCount(secret) + Label.Length];
        var n = Encoding.UTF8.GetBytes(secret, bytes);
        Label.CopyTo(bytes.AsSpan(n));
        _key = SHA256.HashData(bytes);
    }

    private static readonly byte[] Label = "dmart-session-token-v1"u8.ToArray();

    public string Hash(string rawToken)
    {
        Span<byte> mac = stackalloc byte[32];
        var written = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(rawToken), mac);
        return Convert.ToHexString(mac[..written]).ToLowerInvariant();
    }
}
