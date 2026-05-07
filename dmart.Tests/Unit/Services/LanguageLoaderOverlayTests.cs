using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the LanguageLoader operator-overlay (strategy 3): a JSON file at
// ~/.dmart/languages/<stem>.json merges per-key on top of the embedded copy
// and the {BaseDir}/languages fallback. A single override key replaces just
// that key — every other entry stays at its embedded value.
//
// HOME is overridden for the duration of the test so we don't touch the dev
// machine's real ~/.dmart/. The class is non-parallel because env-var state
// is process-global; xunit serializes tests within a class by default.
public sealed class LanguageLoaderOverlayTests : IDisposable
{
    private readonly string _tmpHome = Path.Combine(
        Path.GetTempPath(),
        $"dmart-langtest-{Guid.NewGuid():N}");
    private readonly string? _origHome;

    public LanguageLoaderOverlayTests()
    {
        _origHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", _tmpHome);
        Directory.CreateDirectory(Path.Combine(_tmpHome, ".dmart", "languages"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _origHome);
        if (Directory.Exists(_tmpHome)) Directory.Delete(_tmpHome, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Overlay_Replaces_Single_Key_And_Preserves_Embedded_Rest()
    {
        // Override only `otp_message` for English — every other embedded key
        // (invitation_message, reset_message, ...) must remain intact.
        File.WriteAllText(
            Path.Combine(_tmpHome, ".dmart", "languages", "english.json"),
            """{ "otp_message": "Custom code: {code}" }""");

        var loader = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        loader.Load();

        loader.Get(Language.En, "otp_message").ShouldBe("Custom code: {code}");
        loader.Get(Language.En, "invitation_message").ShouldNotBeNullOrWhiteSpace();
        loader.Get(Language.En, "invitation_message").ShouldNotBe("Custom code: {code}");
    }

    [Fact]
    public void Overlay_Adds_New_Key_To_Existing_Locale()
    {
        // Adding a key that isn't in the embedded file should make Get return
        // the new value for that locale.
        File.WriteAllText(
            Path.Combine(_tmpHome, ".dmart", "languages", "english.json"),
            """{ "custom_key": "operator-defined" }""");

        var loader = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        loader.Load();

        loader.Get(Language.En, "custom_key").ShouldBe("operator-defined");
    }

    [Fact]
    public void Overlay_For_Locale_Without_Embedded_Source_Creates_New_Locale_Map()
    {
        // Strategy 3 must work even for a locale that has no embedded file —
        // operator drops a brand-new locale at ~/.dmart/languages/foo.json
        // and Get(Language.Fr, ...) etc. should resolve.
        File.WriteAllText(
            Path.Combine(_tmpHome, ".dmart", "languages", "french.json"),
            """{ "otp_message": "Votre code OTP est {code}" }""");

        var loader = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        loader.Load();

        loader.Get(Language.Fr, "otp_message").ShouldBe("Votre code OTP est {code}");
    }
}
