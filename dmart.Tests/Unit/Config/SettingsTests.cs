using Dmart.Config;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Mirrors dmart's pytests/get_settings_test.py — validates settings binding.
public class SettingsTests
{
    [Fact]
    public void DmartSettings_Defaults_Are_Sensible()
    {
        var s = new DmartSettings();
        s.JwtIssuer.ShouldBe("dmart");
        s.JwtAudience.ShouldBe("dmart");
        s.JwtAccessMinutes.ShouldBeGreaterThan(0);
        s.ManagementSpace.ShouldBe("management");
    }

    [Fact]
    public void DmartSettings_Binds_From_Configuration()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32",
                ["Dmart:JwtAccessMinutes"] = "30",
                ["Dmart:AdminPassword"] = "hunter22hunter",
            })
            .Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        s.JwtSecret.ShouldBe("test-secret-test-secret-test-secret-32");
        s.JwtAccessMinutes.ShouldBe(30);
        s.AdminPassword.ShouldBe("hunter22hunter");
    }
}
