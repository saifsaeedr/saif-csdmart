using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins.BuiltIn;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Pins the bulk-import audit-skip contract added in PR #24: AuditPlugin reads
// Event.IsBulkImport and short-circuits when true, so a multi-row CSV import
// doesn't produce one audit line per row.
//
// Unit-level — no DB, no HTTP, no factory. We pass a recording ILogger directly
// to AuditPlugin and inspect the captured log records. That isolates the
// contract from the test factory's LogLevel filter (which is Error-only) and
// avoids the HTTP round-trip needed to drive the AuditPlugin via PluginManager.
public sealed class AuditPluginBulkImportSkipTests
{
    [Fact]
    public async Task AuditPlugin_SkipsPerRowEntries_DuringBulkImport()
    {
        var sink = new RecordingLogger();
        var plugin = new AuditPlugin(sink);

        // Three "rows" of a bulk import — each tagged IsBulkImport=true.
        foreach (var sn in new[] { "row_a", "row_b", "row_c" })
            await plugin.HookAsync(BuildEvent(sn, isBulkImport: true));

        sink.Records.Count.ShouldBe(0,
            "AuditPlugin must short-circuit when Event.IsBulkImport is true");
    }

    [Fact]
    public async Task AuditPlugin_StillLogsRegularEvents()
    {
        var sink = new RecordingLogger();
        var plugin = new AuditPlugin(sink);

        // Counter-test: a non-bulk event must still produce one audit entry —
        // confirms the skip path doesn't also catch normal traffic.
        await plugin.HookAsync(BuildEvent("normal_row", isBulkImport: false));

        sink.Records.Count.ShouldBe(1);
        // The message template references the row's shortname so an operator
        // can grep the audit trail by name.
        sink.Records[0].FormattedMessage.ShouldContain("normal_row");
    }

    private static Event BuildEvent(string shortname, bool isBulkImport) => new()
    {
        SpaceName = "itest_audit",
        Subpath = "items",
        Shortname = shortname,
        ActionType = ActionType.Create,
        ResourceType = ResourceType.Content,
        UserShortname = "dmart",
        IsBulkImport = isBulkImport,
    };

    // Minimal in-memory ILogger<AuditPlugin>. Captures every Log() call as a
    // (level, formatted message) pair — enough to assert presence/absence
    // without pulling in a full logging framework.
    private sealed class RecordingLogger : ILogger<AuditPlugin>
    {
        public readonly List<(LogLevel Level, string FormattedMessage)> Records = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, formatter(state, exception)));
        }
    }
}
