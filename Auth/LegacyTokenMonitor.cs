using System.Collections.Concurrent;

namespace Dmart.Auth;

// Counts (and rate-limited warn-logs) accepted JWTs that lack the token_use
// claim — tokens minted before the 2026-06 hardening pass. Python dmart
// (EOL) never wrote the claim; every token the C# issuer mints carries it,
// so this monitor measures the legacy installed base. Once a deployment
// stops recording for a full JwtAccessExpires window, the operator can set
// JWT_REQUIRE_TOKEN_USE=true and claimless tokens are rejected outright.
public sealed class LegacyTokenMonitor(ILogger<LegacyTokenMonitor>? logger = null)
{
    private long _count;
    // subject -> unix seconds of the last warning, so a busy legacy client
    // produces one log line per hour instead of one per request.
    private readonly ConcurrentDictionary<string, long> _lastWarned = new();

    public long Count => Interlocked.Read(ref _count);

    public void Record(string? subject, string context)
    {
        Interlocked.Increment(ref _count);
        if (logger is null) return;

        var key = subject ?? "(unknown)";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Bound memory: the map only grows with distinct legacy subjects,
        // which should trend to zero — but don't trust that unconditionally.
        if (_lastWarned.Count > 10_000) _lastWarned.Clear();
        var last = _lastWarned.GetOrAdd(key, long.MinValue);
        if (last != long.MinValue && now - last < 3600) return;
        if (!_lastWarned.TryUpdate(key, now, last)) return;

        logger.LogWarning(
            "Accepted legacy JWT without token_use claim (subject={Subject}, via={Context}, " +
            "total={Count}). Such tokens predate the 2026-06 hardening; once this warning " +
            "stops appearing for a full token lifetime, set JWT_REQUIRE_TOKEN_USE=true.",
            key, context, Count);
    }
}
