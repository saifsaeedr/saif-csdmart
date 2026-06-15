using System.Security.Cryptography;
using System.Text;
using Dmart.Config;

namespace Dmart.Auth;

// Deterministic, keyed hash for the `otp.value -> code` field. The OTP store
// must not hold the raw 6-digit code (a DB read — backup leak, replica access,
// SQL injection — should not yield a live, replayable credential within its
// ~300s TTL), but verification must stay a single deterministic compare so the
// hot auth path doesn't pay a per-row KDF cost.
//
// HMAC-SHA256(key, code) gives us both:
//   * The OTP keyspace is tiny (10^6 for a 6-digit code), so an UNkeyed hash
//     (plain SHA-256) would be brute-forced offline in milliseconds. The
//     server-side key turns the stored hash into a pepper an attacker can't
//     reverse without also exfiltrating the secret — a DB-only compromise is
//     useless.
//   * Hashing is deterministic, so VerifyAndConsumeAsync stays a single SELECT
//     plus a FixedTimeEquals over the hashes (no per-row Verify pass, which an
//     Argon2-grade KDF would make prohibitively expensive at OTP-verify rates).
//
// The key is derived from `JwtSecret` so operators don't need a second secret.
// Rotating JWT_SECRET implicitly invalidates any in-flight OTP rows (they fail
// to verify and the user re-requests) — acceptable for a ~5-minute TTL. We mix
// in a domain-separation label distinct from SessionTokenHasher's so the same
// secret material never produces a value usable across the two contexts.
//
// Output: lowercase hex (Convert.ToHexString().ToLowerInvariant()) — keeps the
// HSTORE value ASCII, fixed-width (64 chars), and dump-safe.
public sealed class OtpHasher
{
    private readonly byte[] _key;

    public OtpHasher(DmartSettings settings)
    {
        var secret = settings.JwtSecret ?? "";
        // Domain-separated key: SHA256(secret || "dmart-otp-v1"). Wrapping the
        // secret through SHA256 also normalises any input length to 32 bytes
        // (HMAC-SHA256's internal block-size optimum), so callers don't need to
        // worry about JwtSecret being short or oversized.
        var bytes = new byte[Encoding.UTF8.GetByteCount(secret) + Label.Length];
        var n = Encoding.UTF8.GetBytes(secret, bytes);
        Label.CopyTo(bytes.AsSpan(n));
        _key = SHA256.HashData(bytes);
    }

    private static readonly byte[] Label = "dmart-otp-v1"u8.ToArray();

    public string Hash(string code)
    {
        Span<byte> mac = stackalloc byte[32];
        var written = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(code), mac);
        return Convert.ToHexString(mac[..written]).ToLowerInvariant();
    }
}
