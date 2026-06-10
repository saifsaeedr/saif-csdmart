using System.Text.Json;

namespace Dmart.Auth;

// Reads the `token_use` claim from a raw JWT. Callers verify the signature
// first (JwtIssuer.Validate or the JwtBearer middleware) — this is purely a
// payload decode, shared by the bearer pipeline, the OAuth refresh grant and
// the WebSocket handler so the claim is parsed exactly one way everywhere.
public static class TokenUse
{
    public const string Access = "access";
    public const string Refresh = "refresh";

    // Returns "access" / "refresh", or null when the claim is absent or the
    // token is malformed (claimless = minted before the 2026-06 hardening).
    public static string? Read(string rawJwt)
    {
        var parts = rawJwt.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            using var doc = JsonDocument.Parse(Convert.FromBase64String(padded));
            return doc.RootElement.TryGetProperty("token_use", out var tu)
                ? tu.GetString() : null;
        }
        catch { return null; }
    }
}
