using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Auth.OAuth;

// Validates Apple-issued ID tokens AND drives the auth-code → id_token
// exchange used by the web-callback flow. Apple doesn't have a tokeninfo-
// style introspection endpoint — we verify the JWT's signature ourselves
// against Apple's published JWKS (JSON Web Key Set at
// https://appleid.apple.com/auth/keys).
//
// id_token validation flow:
//   1. Split the JWT into header.payload.signature.
//   2. Parse the header, extract `kid` and `alg` (must be RS256).
//   3. Fetch JWKS (cached for 1 hour — Apple rotates roughly weekly, so
//      hourly refresh is safe + cheap).
//   4. Find the JWK whose `kid` matches. Decode `n` and `e` from base64url,
//      construct an RSA public key from those components.
//   5. Verify the RS256 signature over the "header.payload" string.
//   6. Parse the payload, check `iss == https://appleid.apple.com`,
//      `aud == AppleClientId`, and `exp` in the future.
//
// Code-exchange flow (`ExchangeCodeAsync`):
//   1. Mint a short-lived (5-minute) ES256 client_assertion JWT signed with
//      AppleP8PrivateKey. Header carries kid=AppleKeyId; payload carries
//      iss=AppleTeamId, sub=AppleClientId, aud=https://appleid.apple.com,
//      iat=now, exp=now+5min. This replaces Apple's `client_secret` field —
//      Apple verifies our assertion against the public half of our key.
//   2. POST application/x-www-form-urlencoded to
//      https://appleid.apple.com/auth/token with
//      client_id, client_secret=<assertion>, code, grant_type=authorization_code,
//      redirect_uri=AppleOauthCallback.
//   3. Parse the JSON response, validate the returned id_token via the same
//      JWKS path used by ValidateIdTokenAsync. Return OAuthUserInfo.
//
// The client_assertion is cached and reused across requests until it's
// within 30s of expiry, so a burst of logins doesn't re-sign per request.
// Fully AOT-safe — no System.IdentityModel.Tokens.Jwt, no reflection.
public sealed class AppleProvider(
    IHttpClientFactory httpFactory,
    IOptions<DmartSettings> settings,
    ILogger<AppleProvider> log)
{
    private const string JwksUrl = "https://appleid.apple.com/auth/keys";
    private const string TokenUrl = "https://appleid.apple.com/auth/token";
    private const string ExpectedIssuer = "https://appleid.apple.com";
    private static readonly TimeSpan JwksCacheTtl = TimeSpan.FromHours(1);

    // Apple caps client_assertion exp at 6 months but recommends short-lived
    // tokens. 5 minutes is plenty for a single token exchange and keeps the
    // window of credential exposure tight if logs / memory dumps leak.
    private static readonly TimeSpan ClientAssertionTtl = TimeSpan.FromMinutes(5);
    // Safety margin: refresh the cached assertion 30s before its exp so we
    // never hand Apple a JWT that expires mid-request.
    private static readonly TimeSpan ClientAssertionRefreshLeeway = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _jwksLock = new(1, 1);
    private Dictionary<string, RsaPublicKey>? _jwks;
    private DateTime _jwksFetchedAt = DateTime.MinValue;

    private readonly SemaphoreSlim _assertionLock = new(1, 1);
    private string? _cachedAssertion;
    private DateTime _cachedAssertionExp = DateTime.MinValue;

    // Mobile / id-token flow only needs the audience id. The web-callback
    // flow needs the full credential set (id + team + key id + .p8 + the
    // registered redirect_uri) — it's gated separately by SupportsCodeExchange.
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Value.AppleClientId);

    public bool SupportsCodeExchange
    {
        get
        {
            var s = settings.Value;
            return !string.IsNullOrWhiteSpace(s.AppleClientId)
                && !string.IsNullOrWhiteSpace(s.AppleTeamId)
                && !string.IsNullOrWhiteSpace(s.AppleKeyId)
                && !string.IsNullOrWhiteSpace(s.AppleP8PrivateKey)
                && !string.IsNullOrWhiteSpace(s.AppleOauthCallback);
        }
    }

    public async Task<OAuthUserInfo?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(idToken)) return null;

        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            log.LogWarning("apple id_token: not a valid JWT (need 3 parts)");
            return null;
        }

        string headerJson, payloadJson;
        byte[] signature;
        try
        {
            headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            signature = Base64UrlDecode(parts[2]);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "apple id_token: base64url decode failed");
            return null;
        }

        string? kid, alg;
        using (var headerDoc = JsonDocument.Parse(headerJson))
        {
            kid = GetString(headerDoc.RootElement, "kid");
            alg = GetString(headerDoc.RootElement, "alg");
        }
        if (!string.Equals(alg, "RS256", StringComparison.Ordinal))
        {
            log.LogWarning("apple id_token: unsupported alg {Alg}", alg);
            return null;
        }
        if (string.IsNullOrEmpty(kid))
        {
            log.LogWarning("apple id_token: missing kid");
            return null;
        }

        var keyOpt = await GetJwkAsync(kid, ct);
        if (keyOpt is not RsaPublicKey key)
        {
            log.LogWarning("apple id_token: no JWKS entry for kid {Kid}", kid);
            return null;
        }

        // Verify RS256 over "header.payload" — the raw bytes of the signing input.
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent });
        var signatureValid = rsa.VerifyData(
            signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!signatureValid)
        {
            log.LogWarning("apple id_token: signature invalid");
            return null;
        }

        // Payload claim checks.
        using var payload = JsonDocument.Parse(payloadJson);
        var root = payload.RootElement;

        var iss = GetString(root, "iss");
        if (!string.Equals(iss, ExpectedIssuer, StringComparison.Ordinal))
        {
            log.LogWarning("apple id_token: iss mismatch {Iss}", iss);
            return null;
        }

        // `aud` may be a string or an array — handle both.
        var audOk = false;
        if (root.TryGetProperty("aud", out var audEl))
        {
            if (audEl.ValueKind == JsonValueKind.String)
                audOk = audEl.GetString() == settings.Value.AppleClientId;
            else if (audEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in audEl.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && el.GetString() == settings.Value.AppleClientId)
                    { audOk = true; break; }
            }
        }
        if (!audOk)
        {
            log.LogWarning("apple id_token: aud mismatch");
            return null;
        }

        if (root.TryGetProperty("exp", out var expEl) && expEl.ValueKind == JsonValueKind.Number)
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds(expEl.GetInt64());
            if (exp < DateTimeOffset.UtcNow)
            {
                log.LogWarning("apple id_token: expired at {Exp}", exp);
                return null;
            }
        }

        var sub = GetString(root, "sub");
        if (string.IsNullOrEmpty(sub)) return null;

        return new OAuthUserInfo(
            Provider: "apple",
            ProviderId: sub,
            Email: GetString(root, "email"),
            // Apple doesn't include the display name in the id_token — it's
            // only passed the first time the user signs in (separate `user`
            // param). Leave blank here; the resolver handles that gracefully.
            FirstName: null,
            LastName: null,
            PictureUrl: null);
    }

    // ---- JWKS cache ----

    private async Task<RsaPublicKey?> GetJwkAsync(string kid, CancellationToken ct)
    {
        var jwks = await EnsureJwksAsync(ct);
        return jwks?.TryGetValue(kid, out var key) == true ? key : null;
    }

    private async Task<Dictionary<string, RsaPublicKey>?> EnsureJwksAsync(CancellationToken ct)
    {
        if (_jwks is not null && (DateTime.UtcNow - _jwksFetchedAt) < JwksCacheTtl)
            return _jwks;

        await _jwksLock.WaitAsync(ct);
        try
        {
            if (_jwks is not null && (DateTime.UtcNow - _jwksFetchedAt) < JwksCacheTtl)
                return _jwks;

            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await http.GetAsync(JwksUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("apple JWKS fetch {Status}", (int)resp.StatusCode);
                return _jwks;  // keep the stale copy if we have one
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var parsed = new Dictionary<string, RsaPublicKey>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("keys", out var keys) &&
                keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in keys.EnumerateArray())
                {
                    var kid = GetString(k, "kid");
                    var kty = GetString(k, "kty");
                    var n   = GetString(k, "n");
                    var e   = GetString(k, "e");
                    if (string.IsNullOrEmpty(kid) || kty != "RSA" ||
                        string.IsNullOrEmpty(n) || string.IsNullOrEmpty(e))
                        continue;
                    try
                    {
                        parsed[kid] = new RsaPublicKey(Base64UrlDecode(n), Base64UrlDecode(e));
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "apple JWKS: failed to decode key {Kid}", kid);
                    }
                }
            }
            _jwks = parsed;
            _jwksFetchedAt = DateTime.UtcNow;
            return _jwks;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "apple JWKS fetch threw");
            return _jwks;
        }
        finally { _jwksLock.Release(); }
    }

    // ---- helpers ----

    private readonly record struct RsaPublicKey(byte[] Modulus, byte[] Exponent);

    internal static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // ====================================================================
    // CODE EXCHANGE (web-callback flow)
    // ====================================================================

    // POST application/x-www-form-urlencoded to Apple's token endpoint with
    // a freshly-minted ES256 client_assertion. On success, validates the
    // returned id_token via the existing JWKS path and returns OAuthUserInfo.
    // Returns null on any failure (config missing, network error, Apple-side
    // rejection, id_token validation failure). The caller renders the public
    // 4xx response.
    public async Task<OAuthUserInfo?> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        if (!SupportsCodeExchange)
        {
            log.LogInformation("apple code exchange: not configured (TeamId/KeyId/P8/Callback required)");
            return null;
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            log.LogWarning("apple code exchange: empty code");
            return null;
        }

        string assertion;
        try { assertion = await GetClientAssertionAsync(ct); }
        catch (Exception ex)
        {
            log.LogError(ex, "apple code exchange: failed to mint client_assertion (check AppleP8PrivateKey)");
            return null;
        }

        var s = settings.Value;
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = s.AppleClientId,
            ["client_secret"] = assertion,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = s.AppleOauthCallback,
        };

        string idToken;
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(form),
            };
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Apple's error body is JSON like {"error":"invalid_grant"}.
                // Log status + the error code (NOT the full body — could echo
                // the assertion on poorly-configured deployments).
                var errCode = TryExtractError(body);
                log.LogWarning("apple code exchange: token endpoint {Status} ({Error})",
                    (int)resp.StatusCode, errCode ?? "unknown");
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var token = GetString(doc.RootElement, "id_token");
            if (string.IsNullOrEmpty(token))
            {
                log.LogWarning("apple code exchange: response missing id_token");
                return null;
            }
            idToken = token;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "apple code exchange: HTTP failure");
            return null;
        }

        return await ValidateIdTokenAsync(idToken, ct);
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return GetString(doc.RootElement, "error");
        }
        catch (JsonException) { return null; }
    }

    // ====================================================================
    // CLIENT_ASSERTION (ES256 JWT)
    // ====================================================================

    // Returns a cached assertion until it's within ClientAssertionRefreshLeeway
    // of expiry; otherwise mints a fresh one. Holds a single-permit semaphore
    // around the mint so a burst of concurrent logins does ONE signing op,
    // not N.
    private async Task<string> GetClientAssertionAsync(CancellationToken ct)
    {
        if (_cachedAssertion is not null
            && DateTime.UtcNow < _cachedAssertionExp - ClientAssertionRefreshLeeway)
        {
            return _cachedAssertion;
        }

        await _assertionLock.WaitAsync(ct);
        try
        {
            if (_cachedAssertion is not null
                && DateTime.UtcNow < _cachedAssertionExp - ClientAssertionRefreshLeeway)
            {
                return _cachedAssertion;
            }

            var s = settings.Value;
            var (jwt, exp) = MintClientAssertion(
                teamId: s.AppleTeamId,
                clientId: s.AppleClientId,
                keyId: s.AppleKeyId,
                p8Pem: s.AppleP8PrivateKey,
                ttl: ClientAssertionTtl);
            _cachedAssertion = jwt;
            _cachedAssertionExp = exp;
            return jwt;
        }
        finally { _assertionLock.Release(); }
    }

    // Mints the JWT. Internal so the unit suite can drive the signing path
    // directly without standing up the HTTP exchange. Returns the compact
    // JWT and its absolute expiry time so the caller can cache it.
    internal static (string Jwt, DateTime Exp) MintClientAssertion(
        string teamId, string clientId, string keyId, string p8Pem, TimeSpan ttl)
    {
        var nowUtc = DateTime.UtcNow;
        var iat = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var exp = new DateTimeOffset(nowUtc.Add(ttl)).ToUnixTimeSeconds();

        // Header — minimal JSON with stable key order so the same inputs
        // produce the same compact JWT (helps tests pin the format).
        var header = $"{{\"alg\":\"ES256\",\"kid\":\"{JsonEscape(keyId)}\",\"typ\":\"JWT\"}}";
        var payload = $"{{\"iss\":\"{JsonEscape(teamId)}\","
                    + $"\"iat\":{iat},"
                    + $"\"exp\":{exp},"
                    + $"\"aud\":\"{ExpectedIssuer}\","
                    + $"\"sub\":\"{JsonEscape(clientId)}\"}}";
        var signingInput = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(header))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}";

        // Apple's .p8 keys are PKCS#8-encoded EC P-256 private keys — the
        // PEM armor wraps base64 of that DER. ImportFromPem handles both
        // PRIVATE KEY (PKCS#8) and EC PRIVATE KEY (SEC1) labels.
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(p8Pem);

        // SignData returns IEEE P1363 (R || S, fixed-width) by default in
        // .NET 6+ — exactly what JWT/JWS expects for ES256. NOT DER, which
        // is the historical .NET Framework default and would fail to verify
        // on Apple's end.
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var jwt = $"{signingInput}.{Base64UrlEncode(sig)}";
        return (jwt, nowUtc.Add(ttl));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        var s = Convert.ToBase64String(bytes);
        // Strip padding and swap URL-unsafe chars — same convention as
        // Base64UrlDecode above, kept symmetric.
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Minimal JSON-string escaper for the small set of characters that can
    // appear in Apple Developer IDs (printable ASCII). We don't trust the
    // operator to have already JSON-escaped these, but we also don't want
    // to drag in System.Text.Json for a 60-character header string.
    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
