using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pure-function parity tests for InvitationService — the localized SMS template
// (now sourced from LanguageLoader, the single source of truth) and the
// activation email subject + body (now both sourced through Scriban: subject
// from LanguageLoader["activation_subject"], body from
// ActivationTemplateLoader). These are the strings recipients actually see, so
// silent drift here is high-cost.
//
// Cross-references:
//   * dmart/languages/{english,arabic,kurdish}.json -> "invitation_message" /
//     "reset_message" / "activation_subject"
//   * dmart/templates/ActivationEmailContent.txt (embedded default)
//   * dmart/utils/templates/activation.html.j2 (Python parity source)
//   * dmart/utils/generate_email.py::generate_subject("activation")
//
// Shares "dmart-home-overlay" with the *OverlayTests so the static loader
// fields (initialized lazily on first test-method execution) read the real
// HOME rather than an overlay test's redirected one. Without this, the
// loaders cache the overlay test's tmp template into the static field, and
// every parity assertion below reads from that stale cache.
[Collection(HomeOverlayCollection.Name)]
public class InvitationServiceParityTests
{
    // Both loaders read JSON / template files embedded into Dmart's main
    // assembly via `<EmbeddedResource Include="languages/*.json" .../>` and
    // `<EmbeddedResource Include="templates/*.txt" .../>` — the same content
    // the running server consumes, so these tests exercise the production
    // path.
    private static readonly LanguageLoader Languages = MakeLanguages();
    private static readonly ActivationTemplateLoader ActivationTemplates = MakeTemplates();

    private static LanguageLoader MakeLanguages()
    {
        var l = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        l.Load();
        return l;
    }

    private static ActivationTemplateLoader MakeTemplates()
    {
        var t = new ActivationTemplateLoader(NullLogger<ActivationTemplateLoader>.Instance);
        t.Load();
        return t;
    }

    [Fact]
    public void InvitationMessage_English_HasLinkToken_AndContainsExpectedText()
    {
        var msg = Languages.Get(Language.En, "invitation_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("48 hours");
    }

    [Fact]
    public void InvitationMessage_Arabic_IsArabicScript_AndHasLinkToken()
    {
        var msg = Languages.Get(Language.Ar, "invitation_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("تهانينا");
    }

    [Fact]
    public void InvitationMessage_Kurdish_IsKurdishScript_AndHasLinkToken()
    {
        var msg = Languages.Get(Language.Ku, "invitation_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("بەستەرەوە");
    }

    // Languages without a Python entry (Fr, Tr) must fall through to English
    // rather than KeyError. The C# behaviour is more permissive than Python's
    // dict access — the test pins the chosen tradeoff.
    [Theory]
    [InlineData(Language.Fr)]
    [InlineData(Language.Tr)]
    public void InvitationMessage_UnmappedLanguages_FallBack_To_English(Language lang)
    {
        Languages.Get(lang, "invitation_message")
            .ShouldBe(Languages.Get(Language.En, "invitation_message"));
    }

    [Fact]
    public void ResetMessage_English_HasLinkToken_AndMentionsResetWording()
    {
        var msg = Languages.Get(Language.En, "reset_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("password reset");
    }

    [Fact]
    public void ResetMessage_Arabic_IsArabicScript_AndHasLinkToken()
    {
        var msg = Languages.Get(Language.Ar, "reset_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("كلمة المرور");
    }

    [Fact]
    public void ResetMessage_Kurdish_IsKurdishScript_AndHasLinkToken()
    {
        var msg = Languages.Get(Language.Ku, "reset_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("وشەی نهێنی");
    }

    [Theory]
    [InlineData(Language.Fr)]
    [InlineData(Language.Tr)]
    public void ResetMessage_UnmappedLanguages_FallBack_To_English(Language lang)
    {
        Languages.Get(lang, "reset_message")
            .ShouldBe(Languages.Get(Language.En, "reset_message"));
    }

    [Fact]
    public void Get_UnknownKey_ReturnsNull()
    {
        Languages.Get(Language.En, "this_key_does_not_exist").ShouldBeNull();
    }

    // The English subject must still match Python's
    // generate_subject("activation") wording — it's the exact string copy/
    // pasted from there, and changing it would surprise existing recipients.
    [Fact]
    public void ActivationSubject_English_Matches_PythonGenerateSubject()
    {
        Languages.Get(Language.En, "activation_subject").ShouldBe("Welcome to our Platform!");
    }

    // Per-locale visibility for the activation subject — mirrors the
    // InvitationMessage_{Arabic,Kurdish} pattern above so a future regression
    // like "someone deletes activation_subject from arabic.json" trips a
    // pinned test, not just the generic Get_UnknownKey_ReturnsNull below.
    [Fact]
    public void ActivationSubject_Arabic_IsArabicScript()
    {
        var subject = Languages.Get(Language.Ar, "activation_subject").ShouldNotBeNull();
        subject.ShouldContain("منصتنا");
    }

    [Fact]
    public void ActivationSubject_Kurdish_IsKurdishScript()
    {
        var subject = Languages.Get(Language.Ku, "activation_subject").ShouldNotBeNull();
        subject.ShouldContain("پلاتفۆرمەکەمان");
    }

    // Subject also flows through the template engine so operators can use
    // {{name}} etc. in localized subject strings. For the default English
    // subject (no variables) the rendered result is unchanged.
    [Fact]
    public void ActivationSubject_Render_PreservesPlainStringsWithNoVars()
    {
        var user = NewUser(shortname: "alice");
        var src = Languages.Get(Language.En, "activation_subject").ShouldNotBeNull();
        ActivationTemplates.RenderSubject(src, user, "https://app/x")
            .ShouldBe("Welcome to our Platform!");
    }

    // When the operator localizes the subject with a {{var}} token the
    // renderer substitutes it — proves the subject pipeline is actually
    // wired, not just a passthrough.
    [Fact]
    public void ActivationSubject_Substitutes_VariableTokens()
    {
        var user = NewUser(shortname: "alice",
            displayname: new Translation(En: "Alice Smith"));
        ActivationTemplates.RenderSubject("Welcome, {{name}}!", user, "https://app/x")
            .ShouldBe("Welcome, Alice Smith!");
    }

    // Subject is plain text (NOT HTML), so substituted values MUST NOT be
    // HtmlEncode'd. Body renders escape `&` → `&amp;` because the body is
    // HTML; doing the same to a subject line corrupts what the recipient
    // sees in their inbox. This asymmetry is the load-bearing reason the
    // renderer has separate Body/Subject methods rather than one shared
    // substitute path.
    [Fact]
    public void ActivationSubject_DoesNotHtmlEscape_Ampersand()
    {
        var user = NewUser(shortname: "alice",
            displayname: new Translation(En: "Alice & Bob"));
        ActivationTemplates.RenderSubject("Welcome, {{name}}!", user, "https://app/x")
            .ShouldBe("Welcome, Alice & Bob!");
    }

    // Unmapped locales fall back to English for the subject too — same
    // policy as invitation_message / reset_message above.
    [Theory]
    [InlineData(Language.Fr)]
    [InlineData(Language.Tr)]
    public void ActivationSubject_UnmappedLanguages_FallBack_To_English(Language lang)
    {
        Languages.Get(lang, "activation_subject")
            .ShouldBe(Languages.Get(Language.En, "activation_subject"));
    }

    [Fact]
    public void ActivationEmailHtmlBody_ContainsAllPythonTemplateFields()
    {
        var user = NewUser(shortname: "alice", email: "alice@example.com",
            msisdn: "+96512345678", displayname: new Translation(En: "Alice Smith"));
        var html = ActivationTemplates.RenderHtmlBody(user, "https://app/managed/s/abc123");

        html.ShouldContain("Hi Alice Smith");
        html.ShouldContain("MSISDN: +96512345678");
        html.ShouldContain("Username: alice");
        html.ShouldContain("https://app/managed/s/abc123");
        html.ShouldContain("Welcome, we're happy to see you on board!");
    }

    [Fact]
    public void ActivationEmailTextBody_ContainsAllExpectedFields_NoTags_NoEntities()
    {
        // The embedded text template carries the same scope variables but
        // emits no markup or entities — what the recipient actually sees in
        // a text-only mail client (or the text part of the multipart).
        var user = NewUser(shortname: "alice", email: "alice@example.com",
            msisdn: "+96512345678", displayname: new Translation(En: "Alice Smith"));
        var text = ActivationTemplates.RenderTextBody(user, "https://app/managed/s/abc123");

        text.ShouldContain("Hi Alice Smith");
        text.ShouldContain("MSISDN: +96512345678");
        text.ShouldContain("Username: alice");
        text.ShouldContain("https://app/managed/s/abc123");
        text.ShouldContain("Welcome, we're happy to see you on board!");
        text.ShouldNotContain("<");
        text.ShouldNotContain("&amp;");
        text.ShouldNotContain("&lt;");
    }

    [Fact]
    public void ActivationEmailHtmlBody_FallsBack_To_Shortname_When_DisplayNameMissing()
    {
        var user = NewUser(shortname: "bob", email: "bob@example.com");
        var html = ActivationTemplates.RenderHtmlBody(user, "https://app/managed/s/x");
        html.ShouldContain("Hi bob");
    }

    [Fact]
    public void ActivationEmailTextBody_FallsBack_To_Shortname_When_DisplayNameMissing()
    {
        var user = NewUser(shortname: "bob", email: "bob@example.com");
        var text = ActivationTemplates.RenderTextBody(user, "https://app/managed/s/x");
        text.ShouldContain("Hi bob");
    }

    [Fact]
    public void ActivationEmailHtmlBody_HtmlEncodes_HostileShortname_AndLink()
    {
        // A malicious shortname or a tampered link must not break out of the
        // <a href> attribute or inject a tag into the HTML body. We assert
        // on the structural escapes (< > ") because once those three
        // characters are encoded, "onerror=" / "alert(1)" as plain
        // substrings are inert.
        var user = NewUser(shortname: "<script>alert('xss')</script>",
            displayname: new Translation(En: "<img src=x onerror=alert(1)>"));
        var html = ActivationTemplates.RenderHtmlBody(user,
            "https://app/?x=\"><script>alert(1)</script>");

        // No raw injectable tags from user input survive into the body.
        html.ShouldNotContain("<script>alert");
        html.ShouldNotContain("<img src=x");
        html.ShouldNotContain("\"><script>");

        // The encoded forms are present — recipients still see something.
        html.ShouldContain("&lt;script&gt;");
        html.ShouldContain("&lt;img");
        html.ShouldContain("&quot;&gt;&lt;script&gt;");
    }

    [Fact]
    public void ActivationEmailTextBody_DoesNotHtmlEncode_HostileShortname()
    {
        // The text template is plain text; mail clients render it as
        // text/plain, so escaping is unnecessary (and harmful — recipients
        // would see literal "&lt;" instead of "<"). Hostile substrings
        // appear verbatim; this is safe because no rendering happens.
        var user = NewUser(shortname: "<script>alert('xss')</script>",
            displayname: new Translation(En: "<img src=x onerror=alert(1)>"));
        var text = ActivationTemplates.RenderTextBody(user, "https://app/x");

        // Literal characters, not HTML-encoded.
        text.ShouldContain("<script>alert('xss')</script>");
        text.ShouldContain("<img src=x onerror=alert(1)>");
        text.ShouldNotContain("&lt;");
        text.ShouldNotContain("&amp;");
    }

    [Fact]
    public void ActivationEmailHtmlBody_HandlesNullMsisdn_WithoutCrashing()
    {
        var user = NewUser(shortname: "carol", email: "carol@example.com", msisdn: null);
        var html = ActivationTemplates.RenderHtmlBody(user, "https://app/managed/s/x");
        html.ShouldContain("MSISDN: ");        // empty value still rendered
        html.ShouldContain("Username: carol");
    }

    [Fact]
    public void ActivationEmailTextBody_HandlesNullMsisdn_WithoutCrashing()
    {
        var user = NewUser(shortname: "carol", email: "carol@example.com", msisdn: null);
        var text = ActivationTemplates.RenderTextBody(user, "https://app/managed/s/x");
        text.ShouldContain("MSISDN: ");
        text.ShouldContain("Username: carol");
    }

    private static User NewUser(string shortname, string? email = null, string? msisdn = null,
        Translation? displayname = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        Email = email,
        Msisdn = msisdn,
        Displayname = displayname,
        Type = UserType.Web,
        Language = Language.En,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
