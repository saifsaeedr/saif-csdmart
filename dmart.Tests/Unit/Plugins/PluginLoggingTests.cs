using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dmart.Plugins.Native;
using Dmart.Sdk;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Covers the V4 plugin-logging contract:
//   - NativePluginCallbacks.ClampLevel maps int → LogLevel safely
//   - DmartSdk.Log* falls back to stderr on a V3 host (Version<4 or Log==null)
//   - DmartSdk.Log* invokes the callback on a V4 host with the right args
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
    public unsafe void Sdk_Log_V4_Host_Invokes_Callback_With_Marshalled_Strings()
    {
        ResetCapture();
        var cb = new DmartCallbacks { Version = 4, Log = &CaptureCb };
        DmartSdk.LogInfo(in cb, "hello-from-test", category: "smoke");
        _capturedLevel.ShouldBe(DmartSdk.LogLevelInfo);
        _capturedCategory.ShouldBe("smoke");
        _capturedMessage.ShouldBe("hello-from-test");
    }

    [Fact]
    public unsafe void Sdk_Log_V4_Host_Passes_Null_Category_When_Not_Specified()
    {
        ResetCapture();
        var cb = new DmartCallbacks { Version = 4, Log = &CaptureCb };
        DmartSdk.LogWarn(in cb, "default-category");
        _capturedLevel.ShouldBe(DmartSdk.LogLevelWarn);
        _capturedCategory.ShouldBeNull();
        _capturedMessage.ShouldBe("default-category");
    }

    [Fact]
    public unsafe void Sdk_Log_All_Level_Helpers_Pass_Correct_Level()
    {
        var cb = new DmartCallbacks { Version = 4, Log = &CaptureCb };

        ResetCapture(); DmartSdk.LogTrace(in cb, "t");
        _capturedLevel.ShouldBe(DmartSdk.LogLevelTrace);

        ResetCapture(); DmartSdk.LogDebug(in cb, "d");
        _capturedLevel.ShouldBe(DmartSdk.LogLevelDebug);

        ResetCapture(); DmartSdk.LogInfo(in cb, "i");
        _capturedLevel.ShouldBe(DmartSdk.LogLevelInfo);

        ResetCapture(); DmartSdk.LogWarn(in cb, "w");
        _capturedLevel.ShouldBe(DmartSdk.LogLevelWarn);

        ResetCapture(); DmartSdk.LogError(in cb, "e");
        _capturedLevel.ShouldBe(DmartSdk.LogLevelError);

        ResetCapture(); DmartSdk.LogCritical(in cb, "c");
        _capturedLevel.ShouldBe(DmartSdk.LogLevelCritical);
    }

    // ------------------------------------------------------------------
    // Capture harness for the V4 callback path
    // ------------------------------------------------------------------
    //
    // The SDK frees the marshalled UTF-8 buffers in a `finally` after the
    // callback returns, so we copy the strings out into managed memory
    // inside the callback rather than holding the raw pointers.

    private static int _capturedLevel;
    private static string? _capturedCategory;
    private static string? _capturedMessage;

    private static void ResetCapture()
    {
        _capturedLevel = -1;
        _capturedCategory = null;
        _capturedMessage = null;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void CaptureCb(int level, byte* category, byte* message)
    {
        _capturedLevel = level;
        _capturedCategory = category == null ? null : Marshal.PtrToStringUTF8((IntPtr)category);
        _capturedMessage = message == null ? null : Marshal.PtrToStringUTF8((IntPtr)message);
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
