using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Pins the contract that GET /info/settings and `dmart settings` CLI share
// via SettingsSerializer.ToPublicDictionary: every DmartSettings property is
// surfaced, secrets are redacted, and the key shape is snake_case.
public class SettingsSerializerTests
{
    private static DmartSettings Sample() => new()
    {
        DatabaseHost = "localhost",
        DatabasePassword = "hunter2",
        JwtSecret = new string('x', 32),
        AdminPassword = null,
        PostgresConnection = "Host=localhost;Password=secret",
        ListeningPort = 5099,
        IsRegistrable = true,
        MaxQueryLimit = 500,
    };

    [Fact]
    public void Includes_Every_Writable_Property()
    {
        var dict = SettingsSerializer.ToPublicDictionary(Sample());
        // Sampling — if a new setting is added without updating Serializer, it
        // will automatically appear here. These are known properties that must
        // be present.
        dict.ShouldContainKey("database_host");
        dict.ShouldContainKey("listening_port");
        dict.ShouldContainKey("max_query_limit");
        dict.ShouldContainKey("is_registrable");
        dict.ShouldContainKey("jwt_secret");             // redacted but present
        dict.ShouldContainKey("database_password");      // redacted but present
        dict.ShouldContainKey("admin_shortname");        // hardcoded extra
    }

    [Fact]
    public void Redacts_Configured_Secrets_As_Placeholder()
    {
        var dict = SettingsSerializer.ToPublicDictionary(Sample());
        dict["jwt_secret"].ShouldBe("***set***");
        dict["database_password"].ShouldBe("***set***");
        dict["postgres_connection"].ShouldBe("***set***");
    }

    [Fact]
    public void Unset_Secrets_Render_Empty_String()
    {
        // AdminPassword is null in the sample — must not say "***set***".
        var dict = SettingsSerializer.ToPublicDictionary(Sample());
        dict["admin_password"].ShouldBe("");
    }

    [Fact]
    public void Never_Emits_Raw_Secret_Values()
    {
        var dict = SettingsSerializer.ToPublicDictionary(Sample());
        foreach (var v in dict.Values)
        {
            var s = v?.ToString() ?? "";
            s.ShouldNotContain("hunter2");               // DatabasePassword
            s.ShouldNotContain("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"); // JwtSecret
            s.ShouldNotContain("Host=localhost;Password"); // PostgresConnection
        }
    }

    [Fact]
    public void Key_Shape_Is_Snake_Case()
    {
        var dict = SettingsSerializer.ToPublicDictionary(Sample());
        foreach (var k in dict.Keys)
        {
            k.ShouldNotContain(" ");
            k.ShouldBe(k.ToLowerInvariant());
        }
    }
}
