using System.Text.Json;
using Dmart;
using Dmart.Config;
using Dmart.Plugins.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end check of the V4 plugin-logging pipeline:
//   NativePluginCallbacks.LogCb body (EmitPluginLog)
//     → ILoggerFactory.CreateLogger("plugin.<shortname>[.<sub>]")
//     → FileLoggerProvider → LogSink
//     → JSONL file on disk
//
// Each test is hermetic: it builds its own ServiceProvider with a temp
// LogFile, swaps it into NativePluginCallbacks via SetServicesForTesting,
// runs assertions, then resets shared static state. Tests in this class
// share NativePluginCallbacks.Services / _loggerFactoryCache, so they are
// NOT parallelizable — xUnit runs facts in the same class serially.
//
// [Collection] join: PluginInvocationContext.CurrentShortname is ThreadStatic
// and these tests mutate it. Sharing the collection with
// PluginCallbackHistoryTests forces sequential class execution.
[Collection(PluginInvocationContextCollection.Name)]
public sealed class PluginLogToJsonlTests : IDisposable
{
    private readonly string _logPath;
    private readonly ServiceProvider _services;

    public PluginLogToJsonlTests()
    {
        _logPath = Path.Combine(Path.GetTempPath(), $"dmart-pluginlog-{Guid.NewGuid():N}.ljson.log");
        var settings = new DmartSettings { LogFile = _logPath };
        var sc = new ServiceCollection();
        sc.AddSingleton<IOptions<DmartSettings>>(Options.Create(settings));
        sc.AddSingleton<LogSink>();
        sc.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.Services.AddSingleton<ILoggerProvider>(sp =>
                new FileLoggerProvider(sp.GetRequiredService<LogSink>()));
        });
        _services = sc.BuildServiceProvider();
        // Ensures the LogSink singleton is materialised so the file is
        // opened before the first write — otherwise the "ReadAllLines after
        // dispose" pattern below would race with sink construction.
        _services.GetRequiredService<LogSink>();
        NativePluginCallbacks.SetServicesForTesting(_services);
    }

    public void Dispose()
    {
        NativePluginCallbacks.SetServicesForTesting(null);
        _services.Dispose();
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* best effort */ }
    }

    [Fact]
    public void Plugin_Log_Lands_In_Jsonl_With_Shortname_Category()
    {
        PluginInvocationContext.CurrentShortname = "demo_plugin";
        try
        {
            NativePluginCallbacks.EmitPluginLog(
                level: 2 /* Information */,
                sub: null,
                msg: "hello from plugin");
        }
        finally { PluginInvocationContext.CurrentShortname = null; }

        var line = ReadOneJsonLine();
        line.GetProperty("category").GetString().ShouldBe("plugin.demo_plugin");
        line.GetProperty("level").GetString().ShouldBe("INFO");
        // LogSink tags `plugin.<shortname>` lines with `[<shortname>] ` so
        // operators can grep "[demo_plugin]" without parsing JSON category.
        line.GetProperty("message").GetString().ShouldBe("[demo_plugin] hello from plugin");
    }

    [Fact]
    public void Plugin_Log_With_Subcategory_Joins_Under_Shortname_Prefix()
    {
        PluginInvocationContext.CurrentShortname = "demo_plugin";
        try
        {
            NativePluginCallbacks.EmitPluginLog(2, "events", "user signed in");
        }
        finally { PluginInvocationContext.CurrentShortname = null; }

        var line = ReadOneJsonLine();
        line.GetProperty("category").GetString().ShouldBe("plugin.demo_plugin.events");
        line.GetProperty("message").GetString().ShouldBe("[demo_plugin] user signed in");
    }

    [Fact]
    public void Plugin_Log_Without_Shortname_Falls_Back_To_Unknown()
    {
        // Don't set CurrentShortname — confirms the safety net.
        PluginInvocationContext.CurrentShortname = null;
        NativePluginCallbacks.EmitPluginLog(3 /* Warning */, "boot", "no context");

        var line = ReadOneJsonLine();
        line.GetProperty("category").GetString().ShouldBe("plugin.unknown.boot");
        line.GetProperty("level").GetString().ShouldBe("WARNING");
    }

    [Fact]
    public void Plugin_Log_Truncates_Messages_Past_16Kb()
    {
        var huge = new string('x', 20_000);
        PluginInvocationContext.CurrentShortname = "demo_plugin";
        try
        {
            NativePluginCallbacks.EmitPluginLog(2, null, huge);
        }
        finally { PluginInvocationContext.CurrentShortname = null; }

        var line = ReadOneJsonLine();
        var msg = line.GetProperty("message").GetString()!;
        msg.Length.ShouldBeLessThan(20_000);
        msg.ShouldEndWith("…[truncated]");
    }

    [Fact]
    public void Plugin_Log_Skips_Empty_Messages()
    {
        PluginInvocationContext.CurrentShortname = "demo_plugin";
        try
        {
            NativePluginCallbacks.EmitPluginLog(2, null, "");
            NativePluginCallbacks.EmitPluginLog(2, null, null);
        }
        finally { PluginInvocationContext.CurrentShortname = null; }

        // Force the LogSink to flush its in-memory state and check the file.
        // Both calls should be no-ops, so the file stays empty (zero lines).
        File.Exists(_logPath).ShouldBeTrue();
        File.ReadAllLines(_logPath).Length.ShouldBe(0);
    }

    private JsonElement ReadOneJsonLine()
    {
        // FileLoggerProvider writes synchronously inside the ILogger.Log
        // call, so the line is on disk by the time EmitPluginLog returns.
        // No polling / await needed.
        var lines = File.ReadAllLines(_logPath);
        lines.Length.ShouldBe(1);
        return JsonDocument.Parse(lines[0]).RootElement;
    }
}
