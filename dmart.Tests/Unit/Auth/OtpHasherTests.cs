using Dmart.Auth;
using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

// OtpHasher is the at-rest representation for OTP codes: codes are stored as a
// keyed HMAC, never plaintext, so a DB read (backup leak, replica, injection)
// can't yield a live, replayable 6-digit code within its TTL. Because the
// keyspace is tiny (10^6), an UNkeyed hash would be brute-forced offline in
// milliseconds — the server-side key (peppered from JwtSecret) is what makes a
// DB-only compromise useless. These tests pin that contract.
public class OtpHasherTests
{
    private const string Secret = "test-secret-test-secret-test-secret-32-bytes";

    private static OtpHasher Build(string secret = Secret)
        => new(new DmartSettings { JwtSecret = secret });

    [Fact]
    public void Hash_Is_Deterministic()
    {
        // Verification relies on a deterministic hash so a stored value can be
        // matched by a single equality compare (no per-row KDF on the hot path).
        var h = Build();
        h.Hash("123456").ShouldBe(h.Hash("123456"));
    }

    [Fact]
    public void Hash_Does_Not_Return_The_Plaintext_Code()
    {
        var hash = Build().Hash("123456");
        hash.ShouldNotBe("123456");
        // 32-byte HMAC rendered as lowercase hex → 64 chars, dump-safe ASCII.
        hash.Length.ShouldBe(64);
        hash.ShouldBe(hash.ToLowerInvariant());
        hash.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Fact]
    public void Hash_Is_Keyed_By_The_Secret()
    {
        // The key is the whole point: same code under a different secret must
        // produce a different hash, so an attacker without the secret can't
        // pre-compute the 10^6 possible hashes.
        Build("secret-A-secret-A-secret-A-32bytes!!").Hash("123456")
            .ShouldNotBe(Build("secret-B-secret-B-secret-B-32bytes!!").Hash("123456"));
    }

    [Fact]
    public void Hash_Is_Domain_Separated_From_Session_Token_Hash()
    {
        // OtpHasher and SessionTokenHasher derive from the same JwtSecret; a
        // distinct domain-separation label must keep their outputs unrelated so
        // one column's value is never a usable token in the other context.
        var otp = Build().Hash("123456");
        var session = new SessionTokenHasher(new DmartSettings { JwtSecret = Secret }).Hash("123456");
        otp.ShouldNotBe(session);
    }
}
