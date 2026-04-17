using System.IO;
using Dmart.Config;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Unit tests for the dotenv loader. Verifies:
//   1. Key mapping (UPPER_SNAKE_CASE → Dmart:PascalCase)
//   2. Parser tolerance (comments, quotes, trailing comments, export prefix)
//   3. The end-to-end bind path: load config.env into IConfiguration, then
//      Configure<DmartSettings> should see the values.
//
// Each test writes to a throwaway temp file so the tests don't touch the
// real config.env in the repo root.
public class DotEnvTests
{
    [Fact]
    public void ToConfigurationKey_Maps_Single_Word()
    {
        DotEnv.ToConfigurationKey("JWT_SECRET").ShouldBe("Dmart:JwtSecret");
    }

    [Fact]
    public void ToConfigurationKey_Maps_Multi_Word()
    {
        DotEnv.ToConfigurationKey("DATABASE_HOST").ShouldBe("Dmart:DatabaseHost");
        DotEnv.ToConfigurationKey("ADMIN_SHORTNAME").ShouldBe("Dmart:AdminShortname");
        DotEnv.ToConfigurationKey("DATABASE_POOL_RECYCLE").ShouldBe("Dmart:DatabasePoolRecycle");
    }

    [Fact]
    public void ToConfigurationKey_Handles_Lowercase_Input()
    {
        DotEnv.ToConfigurationKey("mock_otp_code").ShouldBe("Dmart:MockOtpCode");
    }

    [Fact]
    public void Parse_Empty_File_Returns_Empty_Dict()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "");
            DotEnv.Parse(tmp).Count.ShouldBe(0);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Skips_Comments_And_Blank_Lines()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
                # this is a comment

                KEY1=value1
                # another comment
                KEY2=value2
                """);
            var d = DotEnv.Parse(tmp);
            d.Count.ShouldBe(2);
            d["KEY1"].ShouldBe("value1");
            d["KEY2"].ShouldBe("value2");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Strips_Double_Quotes()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "JWT_SECRET=\"ABC def\"\n");
            DotEnv.Parse(tmp)["JWT_SECRET"].ShouldBe("ABC def");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Strips_Single_Quotes()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "DATABASE_NAME='dmart'\n");
            DotEnv.Parse(tmp)["DATABASE_NAME"].ShouldBe("dmart");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Strips_Trailing_Comment_On_Unquoted_Value()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "LISTENING_PORT=5099 # web port\n");
            DotEnv.Parse(tmp)["LISTENING_PORT"].ShouldBe("5099");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Does_Not_Strip_Fragment_On_Url_Without_Space()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            // A URL fragment without a preceding space must NOT be treated as a
            // trailing comment — URL-valued settings like APP_URL can legally
            // carry a #fragment.
            File.WriteAllText(tmp, "APP_URL=http://host/#frag\n");
            DotEnv.Parse(tmp)["APP_URL"].ShouldBe("http://host/#frag");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Tolerates_Export_Prefix()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "export DATABASE_HOST=localhost\n");
            DotEnv.Parse(tmp)["DATABASE_HOST"].ShouldBe("localhost");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Ignores_Lines_Without_Equals()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "just a stray line\nKEY=value\n");
            var d = DotEnv.Parse(tmp);
            d.Count.ShouldBe(1);
            d["KEY"].ShouldBe("value");
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void EndToEnd_DotenvLoad_Binds_To_DmartSettings()
    {
        // Write a config.env-style file, push it through the same IConfiguration
        // pipeline Program.cs uses, and verify Configure<DmartSettings> lands on
        // the expected values.
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
                DATABASE_HOST="prod-db.internal"
                DATABASE_PORT=6543
                DATABASE_USERNAME="dmart_svc"
                DATABASE_PASSWORD="s3cret"
                DATABASE_NAME="dmart_prod"
                JWT_SECRET="test-secret-test-secret-test-secret-32"
                ADMIN_SHORTNAME="alice"
                """);

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kv in DotEnv.Parse(tmp))
                values[DotEnv.ToConfigurationKey(kv.Key)] = kv.Value;

            var cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var s = new DmartSettings();
            cfg.GetSection("Dmart").Bind(s);

            s.DatabaseHost.ShouldBe("prod-db.internal");
            s.DatabasePort.ShouldBe(6543);
            s.DatabaseUsername.ShouldBe("dmart_svc");
            s.DatabasePassword.ShouldBe("s3cret");
            s.DatabaseName.ShouldBe("dmart_prod");
            s.JwtSecret.ShouldBe("test-secret-test-secret-test-secret-32");
            // AdminShortname removed — hardcoded to "dmart" in AdminBootstrap
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void FindConfigFile_Honors_Explicit_Env_Var()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "KEY=value\n");
            Environment.SetEnvironmentVariable("BACKEND_ENV", tmp);
            try
            {
                DotEnv.FindConfigFile().ShouldBe(tmp);
            }
            finally
            {
                Environment.SetEnvironmentVariable("BACKEND_ENV", null);
            }
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void FindConfigFile_Falls_Back_When_Env_Var_Path_Missing()
    {
        Environment.SetEnvironmentVariable("BACKEND_ENV", "/nonexistent/path/config.env");
        try
        {
            // Should fall through to the cwd/home lookup and (in a CI-style
            // scratch dir) return null or whatever real file exists. We only
            // assert that the explicit path is NOT returned, since we can't
            // guarantee the absence of a real config.env in the test cwd.
            var result = DotEnv.FindConfigFile();
            result.ShouldNotBe("/nonexistent/path/config.env");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKEND_ENV", null);
        }
    }
}
