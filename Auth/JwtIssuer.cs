using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

// Hand-rolled HS256 JWT — fully AOT-safe (no reflection, no JwtSecurityTokenHandler).
// The JWT payload is built directly with Utf8JsonWriter so we don't depend on
// source-gen JSON metadata for any value types (string[]/long/etc.) at runtime.
// Microsoft.AspNetCore.Authentication.JwtBearer can validate these as long as it's
// configured with the same SymmetricSecurityKey.
public sealed class JwtIssuer(IOptions<DmartSettings> settings)
{
    private readonly DmartSettings _s = settings.Value;

    public string IssueAccess(string subject, IEnumerable<string>? roles = null)
        => Sign(subject, roles, TimeSpan.FromMinutes(_s.JwtAccessMinutes));

    public string IssueRefresh(string subject)
        => Sign(subject, null, TimeSpan.FromDays(_s.JwtRefreshDays));

    private string Sign(string subject, IEnumerable<string>? roles, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;

        using var payloadStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payloadStream))
        {
            writer.WriteStartObject();
            writer.WriteString("sub", subject);
            writer.WriteString("iss", _s.JwtIssuer);
            writer.WriteString("aud", _s.JwtAudience);
            writer.WriteNumber("iat", now.ToUnixTimeSeconds());
            writer.WriteNumber("exp", now.Add(lifetime).ToUnixTimeSeconds());
            writer.WriteString("jti", Guid.NewGuid().ToString("n"));
            if (roles is not null)
            {
                writer.WriteStartArray("roles");
                foreach (var r in roles) writer.WriteStringValue(r);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        // Hand-crafted header keeps us off any JsonContext for header fields too.
        const string headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var encodedHeader  = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(payloadStream.ToArray());
        var signingInput   = $"{encodedHeader}.{encodedPayload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_s.JwtSecret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var encodedSignature = Base64UrlEncode(signature);
        return $"{signingInput}.{encodedSignature}";
    }

    // Validate a JWT and return a ClaimsPrincipal. Used by the WebSocket
    // handler to authenticate the ?token= query parameter (outside the normal
    // JwtBearer middleware pipeline).
    public System.Security.Claims.ClaimsPrincipal? Validate(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        // Verify signature.
        var signingInput = $"{parts[0]}.{parts[1]}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_s.JwtSecret));
        var expectedSig = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        if (expectedSig != parts[2]) return null;

        // Decode payload.
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        var payload = JsonDocument.Parse(payloadJson).RootElement;

        // Check expiration.
        if (payload.TryGetProperty("exp", out var exp))
        {
            var expTime = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
            if (expTime < DateTimeOffset.UtcNow) return null;
        }

        var sub = payload.TryGetProperty("sub", out var s) ? s.GetString() : null;
        if (sub is null) return null;

        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", sub),
        };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "jwt");
        identity.AddClaim(new(identity.NameClaimType, sub));
        return new System.Security.Claims.ClaimsPrincipal(identity);
    }

    public static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
