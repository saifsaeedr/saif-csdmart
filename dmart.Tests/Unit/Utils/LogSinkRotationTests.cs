using Dmart;
using Dmart.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

// Size-based rotation parity with Python's
// concurrent_log_handler.ConcurrentRotatingFileHandler:
//   - LogMaxBytes triggers rollover when the next line would cross the limit
//   - up to LogBackupCount archives kept as "{file}.1" .. "{file}.N"
//   - LogBackupCount=0 truncates in place instead of archiving
//   - LogMaxBytes<=0 disables rotation entirely
public class LogSinkRotationTests
{
    [Fact]
    public void Rollover_Creates_Backup_When_Limit_Crossed()
    {
        var path = NewTempLog();
        try
        {
            // Tiny limit forces rotation after one record. Each WriteLog line
            // is ~150 bytes (hostname, time, level, etc.), so set 100.
            var s = Options.Create(new DmartSettings
            {
                LogFile = path,
                LogMaxBytes = 100,
                LogBackupCount = 3,
            });
            using (var sink = new LogSink(s))
            {
                sink.WriteLog("cat", LogLevel.Information, "first");
                sink.WriteLog("cat", LogLevel.Information, "second");
                sink.WriteLog("cat", LogLevel.Information, "third");
            }

            File.Exists(path).ShouldBeTrue();
            File.Exists($"{path}.1").ShouldBeTrue();
            // Active file holds the most recent record (third); .1 holds prior.
            File.ReadAllText(path).ShouldContain("third");
            File.ReadAllText($"{path}.1").ShouldNotContain("third");
        }
        finally { CleanupAll(path); }
    }

    [Fact]
    public void Rollover_Caps_Backups_At_BackupCount()
    {
        var path = NewTempLog();
        try
        {
            var s = Options.Create(new DmartSettings
            {
                LogFile = path,
                LogMaxBytes = 50, // every line rotates
                LogBackupCount = 2,
            });
            using (var sink = new LogSink(s))
            {
                for (var i = 0; i < 10; i++)
                    sink.WriteLog("cat", LogLevel.Information, $"msg{i}");
            }

            File.Exists(path).ShouldBeTrue();
            File.Exists($"{path}.1").ShouldBeTrue();
            File.Exists($"{path}.2").ShouldBeTrue();
            // .3 must never appear when backupCount=2.
            File.Exists($"{path}.3").ShouldBeFalse();
        }
        finally { CleanupAll(path); }
    }

    [Fact]
    public void BackupCount_Zero_Truncates_In_Place()
    {
        var path = NewTempLog();
        try
        {
            var s = Options.Create(new DmartSettings
            {
                LogFile = path,
                LogMaxBytes = 50,
                LogBackupCount = 0,
            });
            using (var sink = new LogSink(s))
            {
                sink.WriteLog("cat", LogLevel.Information, "first");
                sink.WriteLog("cat", LogLevel.Information, "second");
            }

            File.Exists(path).ShouldBeTrue();
            // No backups kept.
            File.Exists($"{path}.1").ShouldBeFalse();
        }
        finally { CleanupAll(path); }
    }

    [Fact]
    public void MaxBytes_Zero_Disables_Rotation()
    {
        var path = NewTempLog();
        try
        {
            var s = Options.Create(new DmartSettings
            {
                LogFile = path,
                LogMaxBytes = 0,
                LogBackupCount = 5,
            });
            using (var sink = new LogSink(s))
            {
                for (var i = 0; i < 20; i++)
                    sink.WriteLog("cat", LogLevel.Information, $"msg{i}");
            }

            File.Exists(path).ShouldBeTrue();
            // Rotation disabled — no archives ever produced.
            File.Exists($"{path}.1").ShouldBeFalse();
            // All 20 records land in the single file.
            File.ReadAllLines(path).Length.ShouldBe(20);
        }
        finally { CleanupAll(path); }
    }

    [Fact]
    public void Default_Settings_Match_Python_Upstream()
    {
        // Confirms the headline parity values: 256 MB max, 5 backups.
        var s = new DmartSettings();
        s.LogMaxBytes.ShouldBe(268_435_456L); // 0x10000000
        s.LogBackupCount.ShouldBe(5);
    }

    // ---- helpers ----

    private static string NewTempLog() =>
        Path.Combine(Path.GetTempPath(), $"dmart-rotate-{Guid.NewGuid():N}.ljson.log");

    private static void CleanupAll(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        for (var i = 1; i <= 10; i++)
        {
            var p = $"{path}.{i}";
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }
}
