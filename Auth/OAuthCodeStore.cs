using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Dmart.Auth;

// In-memory store for OAuth 2.1 authorization codes. One entry per
// /oauth/authorize issue; consumed and deleted on /oauth/token exchange.
//
// Codes live for 60 seconds — enough time for a fast redirect-round-trip,
// short enough that a stolen URL is essentially useless. Single-use: the
// code is deleted the moment it's redeemed.
//
// PKCE (RFC 7636) is enforced — every authorization includes a
// code_challenge + code_challenge_method. At token time the client MUST
// present a code_verifier whose transformation matches the stored
// challenge. This lets us safely support public clients (MCP clients
// running in the user's IDE) without a client_secret.
public sealed class OAuthCodeStore
{
    public sealed record AuthCodeEntry(
        string UserShortname,
        string ClientId,
        string RedirectUri,
        string? CodeChallenge,
        string CodeChallengeMethod,
        string Scope,
        DateTime ExpiresAt);

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, AuthCodeEntry> _codes = new();

    public string Issue(string userShortname, string clientId, string redirectUri,
        string? codeChallenge, string? codeChallengeMethod, string scope)
    {
        var code = CryptographicallyRandomToken();
        _codes[code] = new AuthCodeEntry(
            userShortname, clientId, redirectUri,
            codeChallenge,
            string.IsNullOrEmpty(codeChallengeMethod) ? "S256" : codeChallengeMethod!,
            scope, DateTime.UtcNow.Add(Ttl));
        return code;
    }

    // Returns the stored entry if the code exists, isn't expired, and the
    // presented PKCE verifier matches the stored challenge. Removes the
    // code atomically — same code can't be redeemed twice.
    public AuthCodeEntry? Consume(string code, string redirectUri,
        string clientId, string? codeVerifier)
    {
        if (!_codes.TryRemove(code, out var entry)) return null;
        if (entry.ExpiresAt < DateTime.UtcNow) return null;
        if (entry.ClientId != clientId) return null;
        if (entry.RedirectUri != redirectUri) return null;

        // PKCE verification. Spec mandates at least `plain` method support;
        // production clients universally use S256.
        if (entry.CodeChallenge is not null)
        {
            if (string.IsNullOrEmpty(codeVerifier)) return null;
            var computed = entry.CodeChallengeMethod.ToLowerInvariant() switch
            {
                "s256"  => S256Challenge(codeVerifier),
                _       => null,
            };
            if (computed is null || computed != entry.CodeChallenge) return null;
        }

        return entry;
    }

    // Removes expired entries that were never redeemed. Called by
    // OAuthStoreSweeper on a timer so the dictionary doesn't grow unbounded.
    public void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, entry) in _codes)
        {
            if (entry.ExpiresAt < now)
                _codes.TryRemove(key, out _);
        }
    }

    private static string CryptographicallyRandomToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static string S256Challenge(string verifier)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.ASCII.GetBytes(verifier), hash);
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
