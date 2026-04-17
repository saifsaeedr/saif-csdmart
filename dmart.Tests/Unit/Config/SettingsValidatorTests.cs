using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Unit tests for DmartSettingsValidator. Validation runs at startup so a
// misconfiguration (bad port, zero pool size, empty DB host) fails loudly
// rather than producing obscure runtime errors later.
public class SettingsValidatorTests
{
    private static DmartSettings Valid() => new()
    {
        // Leave all defaults in place; defaults should already pass validation.
        JwtSecret = new string('x', 32),
    };

    [Fact]
    public void Defaults_Pass()
    {
        var result = new DmartSettingsValidator().Validate(null, Valid());
        result.Succeeded.ShouldBeTrue($"{string.Join("; ", result.Failures ?? new List<string>())}");
    }

    [Fact]
    public void ListeningPort_Zero_Fails()
    {
        var s = Valid();
        s.ListeningPort = 0;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("ListeningPort");
    }

    [Fact]
    public void ListeningPort_TooHigh_Fails()
    {
        var s = Valid();
        s.ListeningPort = 99999;
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }

    [Fact]
    public void DatabaseHost_Empty_Fails_When_No_ConnString()
    {
        var s = Valid();
        s.DatabaseHost = "";
        s.PostgresConnection = null;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("DatabaseHost");
    }

    [Fact]
    public void DatabaseHost_Empty_Passes_When_ConnString_Set()
    {
        var s = Valid();
        s.DatabaseHost = "";
        s.PostgresConnection = "Host=localhost;Database=dmart";
        new DmartSettingsValidator().Validate(null, s).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void DatabasePoolSize_Zero_Fails()
    {
        var s = Valid();
        s.DatabasePoolSize = 0;
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }

    [Fact]
    public void JwtAccessMinutes_Zero_Fails()
    {
        // Zero-minute tokens would be immediately expired — almost certainly a config mistake.
        var s = Valid();
        s.JwtAccessMinutes = 0;
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }

    [Fact]
    public void JwtSecret_TooShort_Fails()
    {
        var s = Valid();
        s.JwtSecret = "short";
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("JwtSecret");
    }

    [Fact]
    public void Negative_DatabasePort_Fails()
    {
        var s = Valid();
        s.DatabasePort = -1;
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }
}
