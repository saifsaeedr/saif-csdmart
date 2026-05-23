using Dmart.Cli;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Cli;

// Pins the 0600 file-perm contract on ~/.dmart/cli.ini — both directions:
// every write goes through CliIniWriter and lands with 0600, and every
// read via CliSettings.Load() refuses an overly-permissive file.
//
// All tests skip on Windows since the security model is ACLs, not mode
// bits — the production code paths are no-ops there too.
public class CliIniPermissionsTests
{
    [Fact]
    public void WriteSecure_Sets_0600_Perms()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(true, "Windows uses ACLs, not mode bits — skip");
            return;
        }

        var tmp = Path.GetTempFileName();
        try
        {
            CliIniWriter.WriteSecure(tmp, new[] { "shortname=dmart", "password=secret" });
            var mode = File.GetUnixFileMode(tmp);
            mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void WriteSecure_String_Overload_Also_Sets_0600()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(true, "Windows uses ACLs, not mode bits — skip");
            return;
        }

        var tmp = Path.GetTempFileName();
        try
        {
            CliIniWriter.WriteSecure(tmp, "shortname=dmart\npassword=secret\n");
            var mode = File.GetUnixFileMode(tmp);
            mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void Load_Rejects_Cli_Ini_With_0644_Perms()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(true, "perm rejection is Unix-only — skip");
            return;
        }

        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "shortname=alice\npassword=PROBE-PWD\n");
            File.SetUnixFileMode(tmp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead);  // 0644

            Environment.SetEnvironmentVariable("DMART_CLI_CONFIG", tmp);
            try
            {
                var s = CliSettings.Load();
                // Loader rejected the file → defaults stayed in place.
                // The PROBE-PWD never reaches s.Password.
                s.Password.ShouldNotBe("PROBE-PWD");
                s.Shortname.ShouldNotBe("alice");
            }
            finally
            {
                Environment.SetEnvironmentVariable("DMART_CLI_CONFIG", null);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void Load_Accepts_Cli_Ini_With_0600_Perms()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(true, "perm check is Unix-only — skip (Windows path skips the guard)");
            return;
        }

        var tmp = Path.GetTempFileName();
        try
        {
            CliIniWriter.WriteSecure(tmp, new[]
            {
                "shortname=alice",
                "password=GOOD-PWD",
            });

            Environment.SetEnvironmentVariable("DMART_CLI_CONFIG", tmp);
            try
            {
                var s = CliSettings.Load();
                s.Shortname.ShouldBe("alice");
                s.Password.ShouldBe("GOOD-PWD");
            }
            finally
            {
                Environment.SetEnvironmentVariable("DMART_CLI_CONFIG", null);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
