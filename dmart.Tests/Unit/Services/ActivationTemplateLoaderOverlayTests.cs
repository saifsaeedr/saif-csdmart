using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the ActivationTemplateLoader operator-overlay: templates at
// ~/.dmart/ActivationEmailContent.{html,txt} each fully replace their
// embedded counterpart independently. Either, both, or neither override
// can be present.
//
// Mirrors LanguageLoaderOverlayTests in mechanism (HOME redirection during
// the test to avoid touching the dev machine's real ~/.dmart/), but the
// override semantics are different — this is a per-format single-file
// replace, not the per-key merge that the language overlay does.
//
// Shares "dmart-home-overlay" collection with LanguageLoaderOverlayTests so
// the two env-mutating classes never race on HOME at the same time.
[Collection(HomeOverlayCollection.Name)]
public sealed class ActivationTemplateLoaderOverlayTests : IDisposable
{
    private readonly string _tmpHome = Path.Combine(
        Path.GetTempPath(),
        $"dmart-tmpltest-{Guid.NewGuid():N}");
    private readonly string? _origHome;

    public ActivationTemplateLoaderOverlayTests()
    {
        _origHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", _tmpHome);
        Directory.CreateDirectory(Path.Combine(_tmpHome, ".dmart"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _origHome);
        if (Directory.Exists(_tmpHome)) Directory.Delete(_tmpHome, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Override_Html_Only_Uses_Override_For_Html_And_Embedded_For_Text()
    {
        // Only the .html override is present. The HTML render uses the
        // override; the text render uses the embedded .txt (which ships in
        // the assembly) — proving the two formats resolve independently.
        WriteOverride("html", "<p>Hello {{ name }} — activate at {{ link }}.</p>");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("alice", displayname: new Translation(En: "Alice"));
        var html = loader.RenderHtmlBody(user, "https://app/x");
        var text = loader.RenderTextBody(user, "https://app/x");

        html.ShouldBe("<p>Hello Alice — activate at https://app/x.</p>");
        // Text comes from the embedded plain-text template, which contains
        // a stable greeting we can assert on.
        text.ShouldContain("Hi Alice");
        text.ShouldContain("https://app/x");
        text.ShouldNotContain("<p>");
    }

    [Fact]
    public void Override_Text_Only_Uses_Override_For_Text_And_Embedded_For_Html()
    {
        WriteOverride("txt", "Hello {{ name }} — activate at {{ link }}.");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("bob", displayname: new Translation(En: "Bob"));
        var html = loader.RenderHtmlBody(user, "https://app/x");
        var text = loader.RenderTextBody(user, "https://app/x");

        text.ShouldBe("Hello Bob — activate at https://app/x.");
        // HTML comes from the embedded HTML template, which still has the
        // "Hi Bob" greeting wrapped in <p>.
        html.ShouldContain("Hi Bob");
        html.ShouldContain("<p>");
    }

    [Fact]
    public void Override_Both_Uses_Both_Overrides()
    {
        WriteOverride("html", "<h1>Welcome, {{ name }}</h1>");
        WriteOverride("txt", "Welcome, {{ name }}.");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("carol", displayname: new Translation(En: "Carol"));
        loader.RenderHtmlBody(user, "https://app/x").ShouldBe("<h1>Welcome, Carol</h1>");
        loader.RenderTextBody(user, "https://app/x").ShouldBe("Welcome, Carol.");
    }

    [Fact]
    public void Override_Neither_Uses_Both_Embedded_Defaults()
    {
        // No overrides — both renders go through the embedded templates.
        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("dave", displayname: new Translation(En: "Dave"));
        var html = loader.RenderHtmlBody(user, "https://app/x");
        var text = loader.RenderTextBody(user, "https://app/x");

        html.ShouldContain("Hi Dave");
        html.ShouldContain("https://app/x");
        html.ShouldContain("<p>");
        text.ShouldContain("Hi Dave");
        text.ShouldContain("https://app/x");
        text.ShouldNotContain("<p>");
    }

    [Fact]
    public void Override_Empty_File_Falls_Back_To_Embedded()
    {
        // An empty override file is treated as "no override" for that
        // format — loader logs and falls through to the embedded default
        // so an accidentally-truncated override doesn't blank the email.
        WriteOverride("html", string.Empty);
        WriteOverride("txt", string.Empty);

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("erin", displayname: new Translation(En: "Erin"));
        var html = loader.RenderHtmlBody(user, "https://app/x");
        var text = loader.RenderTextBody(user, "https://app/x");

        html.ShouldContain("Hi Erin");
        text.ShouldContain("Hi Erin");
    }

    [Fact]
    public void Override_With_Unknown_Variable_Token_PassesThrough_Verbatim()
    {
        // The hand-rolled replacer has no concept of a "parse error" — any
        // string is a valid template. Unknown {{var}} tokens are left as-is
        // so operators immediately see what wasn't substituted, instead of
        // getting an empty body or a silently-dropped variable.
        WriteOverride("html", "<p>Welcome {{name}} — debug={{unknown_var}}</p>");
        WriteOverride("txt", "Welcome {{name}} — debug={{unknown_var}}");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("frank", displayname: new Translation(En: "Frank"));
        loader.RenderHtmlBody(user, "https://app/x")
            .ShouldBe("<p>Welcome Frank — debug={{unknown_var}}</p>");
        loader.RenderTextBody(user, "https://app/x")
            .ShouldBe("Welcome Frank — debug={{unknown_var}}");
    }

    [Fact]
    public void Override_With_LegacyScribanFilterSyntax_NoLongerInterpreted()
    {
        // Legacy `{{ name | html.escape }}` tokens (from the prior Scriban
        // engine) don't match the new regex — `|` isn't in [a-zA-Z0-9_].
        // Pin the contract for both formats: those tokens pass through
        // verbatim so an operator who hasn't updated their override sees
        // the un-substituted text and knows to migrate.
        WriteOverride("html", "<p>Hi {{ name | html.escape }} ({{name}})</p>");
        WriteOverride("txt", "Hi {{ name | html.escape }} ({{name}})");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("gina", displayname: new Translation(En: "Gina"));
        loader.RenderHtmlBody(user, "https://app/x")
            .ShouldBe("<p>Hi {{ name | html.escape }} (Gina)</p>");
        loader.RenderTextBody(user, "https://app/x")
            .ShouldBe("Hi {{ name | html.escape }} (Gina)");
    }

    [Theory]
    // True: anything that looks like an HTML tag.
    [InlineData("<p>Hello</p>", true)]
    [InlineData("<br>", true)]
    [InlineData("<a href=\"https://x\">link</a>", true)]
    [InlineData("Hi {{name}},\n\n<p>welcome!</p>", true)]
    // False: plain text, even with angle brackets in non-tag contexts.
    [InlineData("Hi {{name}}, activate at {{link}}", false)]
    [InlineData("x < 10 && y > 5", false)]   // arithmetic, no `<word>` shape
    [InlineData("", false)]
    public void LooksLikeHtml_DetectsHtmlSmell(string input, bool expected)
    {
        ActivationTemplateLoader.LooksLikeHtml(input).ShouldBe(expected);
    }

    private void WriteOverride(string ext, string content) =>
        File.WriteAllText(Path.Combine(_tmpHome, ".dmart", $"ActivationEmailContent.{ext}"), content);

    private static ActivationTemplateLoader MakeLoader() =>
        new(NullLogger<ActivationTemplateLoader>.Instance);

    private static User NewUser(string shortname, Translation? displayname = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        Displayname = displayname,
        Type = UserType.Web,
        Language = Language.En,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
