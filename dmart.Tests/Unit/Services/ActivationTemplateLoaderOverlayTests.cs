using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the ActivationTemplateLoader operator-overlay: a Scriban template at
// ~/.dmart/ActivationEmailContent.txt fully replaces the embedded default.
// Mirrors LanguageLoaderOverlayTests in mechanism (HOME redirection during
// the test to avoid touching the dev machine's real ~/.dmart/), but the
// override semantics are different — this is a single-file replace, not the
// per-key merge that the language overlay does.
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
    public void Override_File_Replaces_Embedded_Template()
    {
        // The override is a fully custom Scriban template; the embedded one
        // is ignored. Both `name` and `link` variables resolve.
        File.WriteAllText(
            Path.Combine(_tmpHome, ".dmart", "ActivationEmailContent.txt"),
            "Hello {{ name }} — activate at {{ link }}.");

        var loader = new ActivationTemplateLoader(NullLogger<ActivationTemplateLoader>.Instance);
        loader.Load();

        var user = NewUser("alice", displayname: new Translation(En: "Alice"));
        loader.RenderBody(user, "https://app/x")
            .ShouldBe("Hello Alice — activate at https://app/x.");
    }

    [Fact]
    public void Without_Override_File_Embedded_Default_Is_Used()
    {
        // No ~/.dmart/ActivationEmailContent.txt — loader falls through to
        // the embedded resource (templates/ActivationEmailContent.txt).
        // That template renders the "Hi {{ name }}" greeting from the
        // current activation HTML, so we assert on a stable substring.
        var loader = new ActivationTemplateLoader(NullLogger<ActivationTemplateLoader>.Instance);
        loader.Load();

        var user = NewUser("bob", displayname: new Translation(En: "Bob"));
        var html = loader.RenderBody(user, "https://app/x");
        html.ShouldContain("Hi Bob");
        html.ShouldContain("https://app/x");
    }

    [Fact]
    public void Override_With_Unknown_Variable_Token_PassesThrough_Verbatim()
    {
        // The hand-rolled replacer has no concept of a "parse error" — any
        // string is a valid template. Unknown {{var}} tokens are left as-is
        // so operators immediately see what wasn't substituted, instead of
        // getting an empty body or a silently-dropped variable.
        File.WriteAllText(
            Path.Combine(_tmpHome, ".dmart", "ActivationEmailContent.txt"),
            "Welcome {{name}} — debug={{unknown_var}}");

        var loader = new ActivationTemplateLoader(NullLogger<ActivationTemplateLoader>.Instance);
        loader.Load();

        var user = NewUser("dave", displayname: new Translation(En: "Dave"));
        loader.RenderBody(user, "https://app/x")
            .ShouldBe("Welcome Dave — debug={{unknown_var}}");
    }

    [Fact]
    public void Override_With_LegacyScribanFilterSyntax_NoLongerInterpreted()
    {
        // Legacy `{{ name | html.escape }}` tokens (from the prior Scriban
        // engine) don't match the new regex — `|` isn't in [a-zA-Z0-9_].
        // Pin the contract: those tokens pass through verbatim so an
        // operator who hasn't updated their override sees the un-substituted
        // text and knows to migrate.
        File.WriteAllText(
            Path.Combine(_tmpHome, ".dmart", "ActivationEmailContent.txt"),
            "Hi {{ name | html.escape }} ({{name}})");

        var loader = new ActivationTemplateLoader(NullLogger<ActivationTemplateLoader>.Instance);
        loader.Load();

        var user = NewUser("eve", displayname: new Translation(En: "Eve"));
        loader.RenderBody(user, "https://app/x")
            .ShouldBe("Hi {{ name | html.escape }} (Eve)");
    }

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
