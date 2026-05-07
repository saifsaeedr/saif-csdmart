using System.Net.Http;
using Dmart.Auth;
using Dmart.Config;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

// OtpProvider.Generate routes the mock-code short-circuit per delivery channel:
// the destination shape (msisdn vs email) decides which mock flag applies.
// These tests pin that contract so a regression like "MockSmtpApi=true also
// mocked SMS-delivered OTPs" doesn't reappear silently.
//
// The Send pipeline (HTTP / SMTP) isn't exercised here — Generate doesn't call
// it — so the senders can be constructed with a no-op IHttpClientFactory.
public class OtpProviderTests
{
    private static OtpProvider Build(DmartSettings s)
    {
        var opts = Options.Create(s);
        var sms = new SmsSender(new NoOpHttpClientFactory(), opts, NullLogger<SmsSender>.Instance);
        var smtp = new SmtpSender(opts, NullLogger<SmtpSender>.Instance);
        // Generate-path tests don't exercise message rendering, so a minimal
        // (unloaded) LanguageLoader is sufficient — Get returns null and the
        // Send path falls back to the hardcoded English literal.
        var languages = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        return new OtpProvider(opts, sms, smtp, languages, NullLogger<OtpProvider>.Instance);
    }

    [Fact]
    public void MsisdnDestination_With_MockSmppApi_Returns_MockCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = true, MockSmtpApi = false, MockOtpCode = "111222" });
        otp.Generate("+96599887766").ShouldBe("111222");
    }

    [Fact]
    public void EmailDestination_With_MockSmtpApi_Returns_MockCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = false, MockSmtpApi = true, MockOtpCode = "999000" });
        otp.Generate("a@b.c").ShouldBe("999000");
    }

    // The pre-fix bug: MockSmtpApi=true short-circuited SMS-channel OTPs too.
    // After the fix, the SMS channel only honours MockSmppApi.
    [Fact]
    public void MsisdnDestination_When_Only_SmtpMocked_Returns_RealCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = false, MockSmtpApi = true, MockOtpCode = "111222" });
        var code = otp.Generate("+96599887766");
        code.Length.ShouldBe(6);
        code.ShouldNotBe("111222");
        foreach (var c in code) char.IsDigit(c).ShouldBeTrue();
    }

    [Fact]
    public void EmailDestination_When_Only_SmppMocked_Returns_RealCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = true, MockSmtpApi = false, MockOtpCode = "999000" });
        var code = otp.Generate("a@b.c");
        code.Length.ShouldBe(6);
        code.ShouldNotBe("999000");
    }

    // Shortname-shaped destination matches neither IsMsisdn nor IsEmail. Both
    // mocks active or not, Generate should return a real random code — callers
    // that care about predictable mock codes (password-reset-request) must
    // resolve the shortname to a deliverable identifier before calling.
    [Fact]
    public void ShortnameLikeDestination_With_BothMocks_Returns_RealCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = true, MockSmtpApi = true, MockOtpCode = "777888" });
        var code = otp.Generate("alice");
        code.Length.ShouldBe(6);
        code.ShouldNotBe("777888");
    }

    [Fact]
    public void NoMocks_Returns_SixDigitRandom()
    {
        var otp = Build(new DmartSettings { MockSmppApi = false, MockSmtpApi = false });
        var seen = new HashSet<string>();
        for (var i = 0; i < 8; i++) seen.Add(otp.Generate("+96599887766"));
        // Effectively no chance of collision across 8 calls of cryptographic random.
        seen.Count.ShouldBeGreaterThan(1);
        foreach (var c in seen) c.Length.ShouldBe(6);
    }

    // ---- RenderMessage / language overlay ----

    // Builds an OtpProvider whose LanguageLoader has Load() called against
    // the embedded language resources — covers the production happy path.
    private static OtpProvider BuildWithLoadedLanguages()
    {
        var s = new DmartSettings();
        var opts = Options.Create(s);
        var sms = new SmsSender(new NoOpHttpClientFactory(), opts, NullLogger<SmsSender>.Instance);
        var smtp = new SmtpSender(opts, NullLogger<SmtpSender>.Instance);
        var languages = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        languages.Load();
        return new OtpProvider(opts, sms, smtp, languages, NullLogger<OtpProvider>.Instance);
    }

    [Fact]
    public void RenderMessage_English_Uses_Loaded_Template()
    {
        var otp = BuildWithLoadedLanguages();
        otp.RenderMessage(Language.En, "654321").ShouldBe("Your OTP code is 654321");
    }

    [Fact]
    public void RenderMessage_Arabic_Uses_Loaded_Template()
    {
        // Pinned to the exact wording shipped in languages/arabic.json. If
        // operators want a different message, they override at
        // ~/.dmart/languages/arabic.json (LanguageLoader strategy 3).
        var otp = BuildWithLoadedLanguages();
        otp.RenderMessage(Language.Ar, "987654").ShouldBe("رمز التحقق الخاص بك هو 987654");
    }

    [Fact]
    public void RenderMessage_Kurdish_Uses_Loaded_Template()
    {
        var otp = BuildWithLoadedLanguages();
        otp.RenderMessage(Language.Ku, "112233").ShouldBe("کۆدی پشتڕاستکردنەوەکەت 112233 ە");
    }

    [Fact]
    public void RenderMessage_Falls_Back_To_English_Literal_When_Languages_Empty()
    {
        // Unloaded LanguageLoader → Get returns null → fallback literal kicks
        // in. The hardcoded English literal is intentional: a misconfigured
        // deployment must still send a usable OTP, not an empty body.
        var otp = Build(new DmartSettings());
        otp.RenderMessage(Language.Ar, "424242").ShouldBe("Your OTP code is 424242");
    }

    [Fact]
    public void RenderMessage_Falls_Back_To_English_When_Locale_Lacks_Key()
    {
        // French / Turkish locale files don't ship `otp_message`. LanguageLoader.Get
        // falls back to the English entry — pin that contract here so an
        // operator who adds Fr/Tr without otp_message gets an English OTP
        // instead of a literal "{code}" or empty string.
        var otp = BuildWithLoadedLanguages();
        otp.RenderMessage(Language.Fr, "424242").ShouldBe("Your OTP code is 424242");
    }

    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
