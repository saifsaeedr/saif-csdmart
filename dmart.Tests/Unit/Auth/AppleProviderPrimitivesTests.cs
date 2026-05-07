using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Auth.OAuth;
using Dmart.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

// Unit tests for the low-level primitives inside AppleProvider. Exercising
// the full provider over the network is out of scope — we test the bits
// that are provider-independent: base64url decoding, JWT header parse, and
// RSA roundtrip so we have confidence that the RS256 verification path
// works on bytes we sign ourselves. Also covers MintClientAssertion + the
// SupportsCodeExchange flag — the surface added for the web-callback flow.
public sealed class AppleProviderPrimitivesTests
{
    [Fact]
    public void Base64UrlDecode_Handles_Padding_Variants()
    {
        // "hello" → "aGVsbG8" (base64 "aGVsbG8=" without padding and url-safe).
        var result = AppleProvider.Base64UrlDecode("aGVsbG8");
        Encoding.UTF8.GetString(result).ShouldBe("hello");
    }

    [Fact]
    public void Base64UrlDecode_Converts_Url_Safe_Alphabet()
    {
        // Input uses BOTH url-safe characters (`-`, `_`) AND has length that
        // decodes cleanly once padded. Corresponds to the 6-byte payload
        // { 0xFB, 0xEF, 0xFF, 0xFB, 0xEF, 0xFF } — i.e. base64 "++//++//".
        var result = AppleProvider.Base64UrlDecode("--__--__");
        result.Length.ShouldBe(6);
        result.ShouldBe(new byte[] { 0xFB, 0xEF, 0xFF, 0xFB, 0xEF, 0xFF });
    }

    [Fact]
    public void Base64UrlDecode_Empty_Returns_Empty()
    {
        AppleProvider.Base64UrlDecode("").Length.ShouldBe(0);
    }

    // ---- client_assertion (web-callback flow) ----

    // Generate a fresh P-256 EC key in PKCS#8 PEM format — the same format
    // Apple ships in the .p8 download. Returned alongside its public half
    // so the test can verify the signature MintClientAssertion produces.
    private static (string PrivatePem, ECDsa PublicKey) GenerateP256Pem()
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ec.ExportPkcs8PrivateKeyPem();
        // ExportSubjectPublicKeyInfo is the standard "public half" — wrap it
        // in a fresh ECDsa we can call VerifyData on.
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(ec.ExportSubjectPublicKeyInfo(), out _);
        ec.Dispose();
        return (pem, pub);
    }

    [Fact]
    public void MintClientAssertion_Produces_Valid_ES256_Jwt_With_Apple_Claims()
    {
        var (pem, pub) = GenerateP256Pem();
        try
        {
            var (jwt, exp) = AppleProvider.MintClientAssertion(
                teamId: "TEAM12345A",
                clientId: "com.example.web",
                keyId: "KEYID67890",
                p8Pem: pem,
                ttl: TimeSpan.FromMinutes(5));

            // 1. Compact JWT shape: 3 base64url-encoded segments.
            var parts = jwt.Split('.');
            parts.Length.ShouldBe(3);

            // 2. Header claims (alg, kid, typ).
            var headerJson = Encoding.UTF8.GetString(AppleProvider.Base64UrlDecode(parts[0]));
            using var headerDoc = JsonDocument.Parse(headerJson);
            headerDoc.RootElement.GetProperty("alg").GetString().ShouldBe("ES256");
            headerDoc.RootElement.GetProperty("kid").GetString().ShouldBe("KEYID67890");
            headerDoc.RootElement.GetProperty("typ").GetString().ShouldBe("JWT");

            // 3. Payload claims — Apple's required set.
            var payloadJson = Encoding.UTF8.GetString(AppleProvider.Base64UrlDecode(parts[1]));
            using var payloadDoc = JsonDocument.Parse(payloadJson);
            var p = payloadDoc.RootElement;
            p.GetProperty("iss").GetString().ShouldBe("TEAM12345A");
            p.GetProperty("sub").GetString().ShouldBe("com.example.web");
            p.GetProperty("aud").GetString().ShouldBe("https://appleid.apple.com");
            var iat = p.GetProperty("iat").GetInt64();
            var expClaim = p.GetProperty("exp").GetInt64();
            (expClaim - iat).ShouldBe(300);  // 5-minute TTL

            // 4. Signature verifies under ES256 (IEEE P1363 format).
            var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            var sig = AppleProvider.Base64UrlDecode(parts[2]);
            var verified = pub.VerifyData(signingInput, sig,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            verified.ShouldBeTrue();

            // 5. Returned `exp` matches the JWT claim (sanity check on the
            //    DateTime ↔ unix-seconds round-trip the cache relies on).
            new DateTimeOffset(exp, TimeSpan.Zero).ToUnixTimeSeconds().ShouldBe(expClaim);
        }
        finally { pub.Dispose(); }
    }

    [Fact]
    public void MintClientAssertion_Escapes_Special_Characters_In_Ids()
    {
        // Defensive: nothing in the Apple flow expects quotes / backslashes
        // in the IDs, but the JSON-string path must still be safe if an
        // operator's value happens to contain them. The key check: the
        // resulting payload still parses as JSON.
        var (pem, pub) = GenerateP256Pem();
        try
        {
            var (jwt, _) = AppleProvider.MintClientAssertion(
                teamId: "team\"with\\quotes",
                clientId: "client",
                keyId: "kid",
                p8Pem: pem,
                ttl: TimeSpan.FromMinutes(5));

            var payloadJson = Encoding.UTF8.GetString(
                AppleProvider.Base64UrlDecode(jwt.Split('.')[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            doc.RootElement.GetProperty("iss").GetString().ShouldBe("team\"with\\quotes");
        }
        finally { pub.Dispose(); }
    }

    [Fact]
    public void SupportsCodeExchange_Requires_All_Five_Settings()
    {
        // Mobile/id-token flow only needs AppleClientId; the web-callback
        // flow needs the additional four. Pin the gate so a partial config
        // (e.g. forgot the .p8) fails fast and explicitly instead of
        // hitting Apple's token endpoint with garbage.
        var partial = MakeProvider(new DmartSettings
        {
            AppleClientId = "com.example.web",
            AppleTeamId = "TEAMID",
            AppleKeyId = "KEYID",
            // missing P8 + Callback
        });
        partial.IsConfigured.ShouldBeTrue();
        partial.SupportsCodeExchange.ShouldBeFalse();

        var full = MakeProvider(new DmartSettings
        {
            AppleClientId = "com.example.web",
            AppleTeamId = "TEAMID",
            AppleKeyId = "KEYID",
            AppleP8PrivateKey = "-----BEGIN PRIVATE KEY-----\nfake\n-----END PRIVATE KEY-----\n",
            AppleOauthCallback = "https://example.com/user/apple/callback",
        });
        full.SupportsCodeExchange.ShouldBeTrue();
    }

    private static AppleProvider MakeProvider(DmartSettings s) =>
        new(new NoOpHttpClientFactory(),
            Options.Create(s),
            NullLogger<AppleProvider>.Instance);

    private sealed class NoOpHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }
}
