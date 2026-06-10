using System.Text.Json;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for the resume sidecar used by `dmart import --resume`.
// Three properties under test:
//   1. Markers persist atomically (write to .tmp + rename — a crash
//      mid-write must not leave a half-written JSON that fails parse
//      on the next LoadOrCreate).
//   2. LoadOrCreate round-trips a written checkpoint without loss.
//   3. Corrupt sidecar falls back to a fresh checkpoint (so the next
//      `--resume` run isn't blocked by a half-written file).
public sealed class ImportCheckpointStoreTests : IDisposable
{
    private readonly string _dir;

    public ImportCheckpointStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ckpt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void FreshCheckpoint_HasNoMarkers()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");

        store.IsHeadDone().ShouldBeFalse();
        store.IsTailDone("any-space").ShouldBeFalse();
        store.PassesDone.ShouldBeEmpty();
        store.TailDone.ShouldBeEmpty();
    }

    [Fact]
    public void MarkHeadDone_Persists()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkHeadDone();

        File.Exists(path).ShouldBeTrue("the marker should be flushed to disk on Mark");
        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        reload.IsHeadDone().ShouldBeTrue("reloaded store should see the head marker");
    }

    [Fact]
    public void MarkTailDone_PerSpaceRoundTrip()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkTailDone("applications");
        store.MarkTailDone("products");

        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        reload.IsTailDone("applications").ShouldBeTrue();
        reload.IsTailDone("products").ShouldBeTrue();
        reload.IsTailDone("management").ShouldBeFalse();
    }

    [Fact]
    public void MarkHeadDone_IsIdempotent()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkHeadDone();
        store.MarkHeadDone();  // calling twice must not duplicate the entry
        store.PassesDone.Count.ShouldBe(1, "head marker should be deduped");
    }

    [Fact]
    public void Clear_RemovesSidecar()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkHeadDone();
        File.Exists(path).ShouldBeTrue();

        store.Clear();
        File.Exists(path).ShouldBeFalse("Clear should delete the sidecar on a clean import");
    }

    [Fact]
    public void CorruptSidecar_FallsBackToFreshStore()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        // Write a deliberately broken JSON.
        File.WriteAllText(path, "{this is not, valid json");

        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.IsHeadDone().ShouldBeFalse("a corrupt sidecar should be treated as no checkpoint");
        store.PassesDone.ShouldBeEmpty();
        // Writing to the recovered store should land atomically and become parseable.
        store.MarkHeadDone();
        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        reload.IsHeadDone().ShouldBeTrue();
    }

    [Fact]
    public void CorruptSidecar_LogsWarning_WhenLoggerProvided()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        File.WriteAllText(path, "{this is not, valid json");

        var logger = new CapturingLogger();
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart", logger);

        store.IsHeadDone().ShouldBeFalse();
        logger.Warnings.ShouldNotBeEmpty("a discarded corrupt checkpoint must be surfaced, not silent");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void DefaultPathFor_IsSidecarOfFolder()
    {
        var p = ImportCheckpointStore.DefaultPathFor("/var/lib/dmart/spaces");
        p.ShouldBe(Path.Combine("/var/lib/dmart/spaces", ".dmart-import-checkpoint.json"));
    }
}
