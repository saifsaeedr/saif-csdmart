using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dmart.Plugins.Native;
using Dmart.Sdk;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Covers the plugin-logging contract:
//   - NativePluginCallbacks.ClampLevel maps int → LogLevel safely
//   - DmartSdk.Log* writes to stderr when no plugin log file is open
//     (DMART_PLUGIN_LOG_DIR unset, or SetShortname not called)
//
// The host's V4 cb.Log callback is no longer invoked by DmartSdk.Log* —
// plugins now own their log file via DmartSdk.SetShortname (see
// DmartSdkLoggingTests). The cb argument stays in the SDK signatures for
// source-compat with V4 plugins, but its Log field is intentionally
// unused. These tests confirm the stderr fallback fires regardless of
// whether cb.Version is 3 or 4, and regardless of whether cb.Log is null
// or set.
//
// Both this class and DmartSdkLoggingTests mutate DmartSdk's process-
// global _logStream/_shortname statics, so they share a collection to
// serialize execution under xUnit's default parallel-across-classes runner.
[Collection("PluginSdkLogging")]
public class PluginLoggingTests
{
    [Theory]
    [InlineData(0, LogLevel.Trace)]
    [InlineData(1, LogLevel.Debug)]
    [InlineData(2, LogLevel.Information)]
    [InlineData(3, LogLevel.Warning)]
    [InlineData(4, LogLevel.Error)]
    [InlineData(5, LogLevel.Critical)]
    [InlineData(6, LogLevel.None)]
    public void ClampLevel_Maps_Valid_Values(int raw, LogLevel expected)
        => NativePluginCallbacks.ClampLevel(raw).ShouldBe(expected);

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(99)]
    public void ClampLevel_Coerces_OutOfRange_To_Information(int raw)
        => NativePluginCallbacks.ClampLevel(raw).ShouldBe(LogLevel.Information);

    [Fact]
    public unsafe void Sdk_Log_V3_Host_Falls_Back_To_Stderr()
    {
        var cb = new DmartCallbacks { Version = 3, Log = null };
        var captured = CaptureStderr(() => DmartSdk.LogError(in cb, "boom", category: "events"));
        captured.ShouldContain("[ERROR]");
        captured.ShouldContain("[events]");
        captured.ShouldContain("boom");
    }

    [Fact]
    public unsafe void Sdk_Log_V4_Host_With_Null_Callback_Falls_Back_To_Stderr()
    {
        // Even with Version = 4, if Log is null (defensive), the wrapper
        // should fall back to stderr and not segfault.
        var cb = new DmartCallbacks { Version = 4, Log = null };
        var captured = CaptureStderr(() => DmartSdk.LogInfo(in cb, "hi"));
        captured.ShouldContain("[INFO]");
        captured.ShouldContain("hi");
    }

    [Fact]
    public unsafe void Sdk_Log_V4_Host_With_Live_Callback_Still_Falls_Back_To_Stderr()
    {
        // The new SDK contract ignores cb.Log entirely. Even when a host
        // supplies a non-null V4 callback, DmartSdk.Log* writes to stderr
        // (when no plugin log file is open) instead of invoking the cb.
        var cb = new DmartCallbacks { Version = 4, Log = &NoopCb };
        var captured = CaptureStderr(() =>
            DmartSdk.LogInfo(in cb, "hello-from-test", category: "smoke"));
        captured.ShouldContain("[INFO]");
        captured.ShouldContain("[smoke]");
        captured.ShouldContain("hello-from-test");
    }

    [Fact]
    public unsafe void Sdk_Log_All_Level_Helpers_Emit_Correct_Level_Tag()
    {
        // With no plugin file open, every level helper must emit its own
        // tag on the stderr fallback line. Confirms the level → string
        // mapping is wired into the helpers, not lost in the cb.Log shift.
        var cb = new DmartCallbacks { Version = 4, Log = &NoopCb };
        CaptureStderr(() => DmartSdk.LogTrace(in cb, "t")).ShouldContain("[TRACE]");
        CaptureStderr(() => DmartSdk.LogDebug(in cb, "d")).ShouldContain("[DEBUG]");
        CaptureStderr(() => DmartSdk.LogInfo(in cb, "i")).ShouldContain("[INFO]");
        CaptureStderr(() => DmartSdk.LogWarn(in cb, "w")).ShouldContain("[WARN]");
        CaptureStderr(() => DmartSdk.LogError(in cb, "e")).ShouldContain("[ERROR]");
        CaptureStderr(() => DmartSdk.LogCritical(in cb, "c")).ShouldContain("[CRITICAL]");
    }

    // A do-nothing cb.Log so we can construct a "live V4 host" and verify
    // the SDK still bypasses it. The signature matches DmartCallbacks.Log.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void NoopCb(int level, byte* category, byte* message)
    {
        // intentionally empty — the SDK must not invoke this.
    }

    private static string CaptureStderr(Action action)
    {
        var original = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try { action(); }
        finally { Console.SetError(original); }
        return sw.ToString();
    }
}
