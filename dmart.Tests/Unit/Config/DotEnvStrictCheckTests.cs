using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Pins the strict config.env validation: any key that isn't a DmartSettings
// property (and isn't one of the small BACKEND_ENV / DMART_ENV passthroughs)
// must be reported as an error. This catches typos and stale keys from
// renamed/removed settings that would otherwise silently use defaults.
public class DotEnvStrictCheckTests
{
    [Fact]
    public void Known_Keys_Pass()
    {
        var raw = new Dictionary<string, string>
        {
            ["DATABASE_HOST"] = "localhost",
            ["JWT_SECRET"] = "x",
            ["LISTENING_PORT"] = "8282",
        };
        DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw).ShouldBeEmpty();
    }

    [Fact]
    public void Typo_Is_Flagged()
    {
        var raw = new Dictionary<string, string> { ["DATABAE_HOST"] = "x" };
        var errors = DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw);
        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("DATABAE_HOST");
        errors[0].ShouldContain("/tmp/config.env");
    }

    [Fact]
    public void Removed_Setting_Is_Flagged()
    {
        // REDIS_CONNECTION, OTP_TOKEN_TTL, etc. were deliberately removed —
        // a config.env still carrying them is a mistake worth surfacing.
        var raw = new Dictionary<string, string>
        {
            ["APP_NAME"] = "dmart",                 // removed
            ["USERS_SUBPATH"] = "users",            // removed
            ["ENABLE_SQL_BACKEND"] = "true",        // removed
        };
        var errors = DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw);
        errors.Count.ShouldBe(3);
    }

    [Fact]
    public void Passthrough_Keys_Are_Allowed()
    {
        // BACKEND_ENV / DMART_ENV select WHICH config.env to load — they may
        // legitimately appear inside one of those files without mapping to a
        // DmartSettings property.
        var raw = new Dictionary<string, string>
        {
            ["BACKEND_ENV"] = "/etc/dmart/config.env",
            ["DMART_ENV"] = "/etc/dmart/config.env",
        };
        DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw).ShouldBeEmpty();
    }

    [Fact]
    public void Pascal_To_Snake_Roundtrips_With_ToConfigurationKey()
    {
        // Inverse of DotEnv.ToConfigurationKey — the UPPER_SNAKE form we
        // expect in config.env must map back to the same Dmart:Xxx path.
        foreach (var pascal in new[] { "DatabaseHost", "JwtSecret", "MaxFailedLoginAttempts", "AllowedCorsOrigins" })
        {
            var snake = DotEnvStrictCheck.PascalToUpperSnake(pascal);
            DotEnv.ToConfigurationKey(snake).ShouldBe($"Dmart:{pascal}");
        }
    }
}
