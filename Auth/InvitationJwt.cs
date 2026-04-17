using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Enums;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

// Invitation JWT — Python-compatible wire format:
//
//   Header:  {"alg":"HS256","typ":"JWT"}
//   Payload: {"data":{"shortname":"...","channel":"SMS"|"EMAIL"},"expires":<unix>}
//
// Signed with HS256 using the same JwtSecret as access tokens so a
// Python-issued invitation verifies on C# and vice versa. No standard JWT
// claims (sub/iss/aud/iat/exp/jti) are emitted — Python's decoder reads only
// `data.shortname`, `data.channel`, and `expires`, and any extras would just
// be ignored but are omitted here to keep wire bytes identical.
//
// Unlike access tokens, invitation JWTs are NOT self-sufficient — they must
// be accompanied by a live row in the `invitations` table (see
// InvitationRepository). The DB row is the single-use enforcement: once the
// invitation completes a login, the row is deleted and replays fail even if
// the JWT's `expires` hasn't elapsed yet.
public sealed class InvitationJwt(IOptions<DmartSettings> settings)
{
    private readonly DmartSettings _s = settings.Value;

    public string Mint(string shortname, InvitationChannel channel)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(_s.JwtInvitationDays).ToUnixTimeSeconds();
        var channelWire = channel == InvitationChannel.Email ? "EMAIL" : "SMS";

        using var payloadStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payloadStream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("data");
            writer.WriteString("shortname", shortname);
            writer.WriteString("channel", channelWire);
            writer.WriteEndObject();
            writer.WriteNumber("expires", expires);
            writer.WriteEndObject();
        }

        const string headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var encodedHeader = JwtIssuer.Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = JwtIssuer.Base64UrlEncode(payloadStream.ToArray());
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_s.JwtSecret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{JwtIssuer.Base64UrlEncode(signature)}";
    }

    public bool TryVerify(string token, out string shortname, out InvitationChannel channel)
    {
        shortname = "";
        channel = InvitationChannel.Email;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        // Fixed-time HMAC comparison.
        var signingInput = $"{parts[0]}.{parts[1]}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_s.JwtSecret));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        byte[] received;
        try { received = Base64UrlDecode(parts[2]); }
        catch { return false; }
        if (!CryptographicOperations.FixedTimeEquals(expected, received)) return false;

        // Decode payload.
        string payloadJson;
        try { payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1])); }
        catch { return false; }

        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;

        // Expiry check — Python stores `expires` as a unix epoch seconds number.
        if (!root.TryGetProperty("expires", out var expEl)) return false;
        var expires = expEl.ValueKind == JsonValueKind.Number
            ? expEl.GetInt64()
            : long.TryParse(expEl.GetString(), out var parsed) ? parsed : 0;
        if (expires <= 0) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expires) < DateTimeOffset.UtcNow) return false;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
        if (!data.TryGetProperty("shortname", out var sn) || sn.ValueKind != JsonValueKind.String) return false;
        if (!data.TryGetProperty("channel", out var ch) || ch.ValueKind != JsonValueKind.String) return false;

        shortname = sn.GetString()!;
        var channelRaw = ch.GetString();
        channel = string.Equals(channelRaw, "EMAIL", StringComparison.OrdinalIgnoreCase)
            ? InvitationChannel.Email
            : InvitationChannel.Sms;
        return !string.IsNullOrEmpty(shortname);
    }

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
