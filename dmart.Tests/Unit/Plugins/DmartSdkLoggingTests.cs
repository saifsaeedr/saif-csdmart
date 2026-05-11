using System.Text.Json;
using Dmart.Sdk;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Tests for the SDK-side logging contract in custom_plugins_sdk/shared/
// DmartCallbacks.cs. A plugin calls DmartSdk.SetShortname once, then any
// DmartSdk.LogInfo / LogWarn / LogError call writes a JSON line to
// `<DMART_PLUGIN_LOG_DIR>/<shortname>.ljson.log` — directly, with no
// dmart-host involvement. The cb argument is retained for source-compat
// only; its Log field is not invoked.
//
// SDK statics (_shortname, _logStream, _logPath) are process-global, so
// these tests mutate shared state. xUnit serializes tests in a single
// class, but each test must reset the state in Dispose to keep the suite
// hermetic.
// `DMART_PLUGIN_LOG_DIR` and the DmartSdk._logStream/_shortname statics
// are process-global. xUnit runs different test classes in parallel by
// default, so isolate this class into its own collection to prevent a
// concurrent test from observing or clobbering the shared state.
[Collection("PluginSdkLogging")]
public sealed class DmartSdkLoggingTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _prevEnv;
    private static readonly DmartCallbacks Cb = default;

    public DmartSdkLoggingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dmart-sdklog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _prevEnv = Environment.GetEnvironmentVariable("DMART_PLUGIN_LOG_DIR");
        Environment.SetEnvironmentVariable("DMART_PLUGIN_LOG_DIR", _dir);
    }

    public void Dispose()
    {
        // Re-register with a throwaway shortname to release the open
        // FileStream so the temp dir can be deleted on Windows-like FS
        // semantics (Linux tolerates open handles on delete, but the test
        // is portable).
        Environment.SetEnvironmentVariable("DMART_PLUGIN_LOG_DIR", null);
        DmartSdk.SetShortname("__test_reset__");
        Environment.SetEnvironmentVariable("DMART_PLUGIN_LOG_DIR", _prevEnv);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void LogInfo_Writes_To_Plugin_File_After_SetShortname()
    {
        DmartSdk.SetShortname("oodi_sync");
        DmartSdk.LogPath.ShouldBe(Path.Combine(_dir, "oodi_sync.ljson.log"));

        DmartSdk.LogInfo(in Cb, "hook entry — event=create user");

        var line = JsonDocument.Parse(File.ReadAllLines(DmartSdk.LogPath!).Single()).RootElement;
        line.GetProperty("level").GetString().ShouldBe("INFO");
        line.GetProperty("message").GetString().ShouldBe("hook entry — event=create user");
        line.GetProperty("process").GetInt32().ShouldBe(Environment.ProcessId);
        line.GetProperty("time").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void LogWarn_And_LogError_Use_Correct_Levels()
    {
        DmartSdk.SetShortname("oodi_sync");
        DmartSdk.LogWarn(in Cb, "queue backing up");
        DmartSdk.LogError(in Cb, "smtp failed");

        var lines = File.ReadAllLines(DmartSdk.LogPath!);
        lines.Length.ShouldBe(2);
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("level").GetString().ShouldBe("WARN");
        JsonDocument.Parse(lines[1]).RootElement.GetProperty("level").GetString().ShouldBe("ERROR");
    }

    [Fact]
    public void Category_Field_Appears_When_Provided()
    {
        DmartSdk.SetShortname("oodi_sync");
        DmartSdk.LogInfo(in Cb, "user signed in", category: "events");

        var line = JsonDocument.Parse(File.ReadAllLines(DmartSdk.LogPath!).Single()).RootElement;
        line.GetProperty("category").GetString().ShouldBe("events");
        line.GetProperty("message").GetString().ShouldBe("user signed in");
    }

    [Fact]
    public void Falls_Back_To_Stderr_When_Env_Missing()
    {
        // No SetShortname call with env unset: file logging is inactive
        // and the SDK must NOT throw. We can't easily assert the stderr
        // write here (xunit doesn't intercept Console.Error), but the
        // round-trip below proves the call completes and that LogPath
        // stays null.
        Environment.SetEnvironmentVariable("DMART_PLUGIN_LOG_DIR", null);
        DmartSdk.SetShortname("env_missing");
        DmartSdk.LogPath.ShouldBeNull();
        DmartSdk.LogInfo(in Cb, "should not throw");
        // Nothing landed on disk under the test dir.
        Directory.GetFiles(_dir).Length.ShouldBe(0);
    }

    [Fact]
    public void Rejects_Unsafe_Shortname()
    {
        DmartSdk.SetShortname("../traversal");
        DmartSdk.LogPath.ShouldBeNull();
        DmartSdk.SetShortname("a/b");
        DmartSdk.LogPath.ShouldBeNull();
        // No files created in or above the test dir.
        Directory.GetFiles(_dir).Length.ShouldBe(0);
        Directory.GetFiles(Path.GetDirectoryName(_dir)!, "traversal*").Length.ShouldBe(0);
    }

    [Fact]
    public void Json_Encodes_Embedded_Quotes_And_Newlines()
    {
        DmartSdk.SetShortname("escape_check");
        DmartSdk.LogInfo(in Cb, "she said \"hi\"\nand left");
        var line = JsonDocument.Parse(File.ReadAllLines(DmartSdk.LogPath!).Single()).RootElement;
        line.GetProperty("message").GetString().ShouldBe("she said \"hi\"\nand left");
    }
}
