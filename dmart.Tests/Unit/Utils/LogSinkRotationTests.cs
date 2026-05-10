using Dmart;
using Dmart.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

// Size-based rotation:
//   - LogMaxBytes triggers rollover when the next line would cross the limit
//   - LogBackupCount > 0  : Python parity, "{file}.1" newest .. ".N" oldest, shifted
//   - LogBackupCount == 0 : truncate in place, no archives
//   - LogBackupCount < 0  : unlimited, archives accumulate as "{file}.1", ".2", ...
//   - LogMaxBytes<=0      : disables rotation entirely
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
    public void Default_Settings_Are_1Gb_And_Unlimited_Backups()
    {
        // 1 GB rollover with unlimited archive retention: operators handle
        // long-term cleanup via logrotate / journald, dmart preserves history.
        var s = new DmartSettings();
        s.LogMaxBytes.ShouldBe(1_073_741_824L);
        s.LogBackupCount.ShouldBeLessThan(0);
    }

    [Fact]
    public void Unlimited_Backups_Append_Numbering_Never_Deletes_Archives()
    {
        var path = NewTempLog();
        try
        {
            var s = Options.Create(new DmartSettings
            {
                LogFile = path,
                LogMaxBytes = 50,
                LogBackupCount = -1,
            });
            using (var sink = new LogSink(s))
            {
                for (var i = 0; i < 6; i++)
                    sink.WriteLog("cat", LogLevel.Information, $"msg{i}");
            }

            // First write doesn't rotate (file was empty), so writes 2..6
            // each produce one archive: .1, .2, .3, .4, .5.
            File.Exists(path).ShouldBeTrue();
            for (var i = 1; i <= 5; i++)
                File.Exists($"{path}.{i}").ShouldBeTrue();
            File.Exists($"{path}.6").ShouldBeFalse();

            // Append-numbering invariant: .1 holds the OLDEST archive
            // (msg0 — first record before any rotation), .5 the most
            // recent rotated record.
            File.ReadAllText($"{path}.1").ShouldContain("msg0");
            File.ReadAllText($"{path}.5").ShouldContain("msg4");
            File.ReadAllText(path).ShouldContain("msg5");
        }
        finally { CleanupAll(path); }
    }

    [Fact]
    public void Unlimited_Backups_Resume_Index_After_Restart()
    {
        var path = NewTempLog();
        try
        {
            var s = Options.Create(new DmartSettings
            {
                LogFile = path,
                LogMaxBytes = 50,
                LogBackupCount = -1,
            });

            // First process: produce two archives (.1, .2).
            using (var sink1 = new LogSink(s))
            {
                sink1.WriteLog("cat", LogLevel.Information, "a");
                sink1.WriteLog("cat", LogLevel.Information, "b");
                sink1.WriteLog("cat", LogLevel.Information, "c");
            }
            File.Exists($"{path}.1").ShouldBeTrue();
            File.Exists($"{path}.2").ShouldBeTrue();

            // Second process opens, then forces another rollover. Must
            // pick .3 rather than overwriting .1 — proves the unlimited
            // path scans disk state instead of relying on in-memory counter.
            using (var sink2 = new LogSink(s))
            {
                sink2.WriteLog("cat", LogLevel.Information, "d");
                sink2.WriteLog("cat", LogLevel.Information, "e");
            }
            File.Exists($"{path}.1").ShouldBeTrue();
            File.Exists($"{path}.2").ShouldBeTrue();
            File.Exists($"{path}.3").ShouldBeTrue();
            // .1 must still hold the oldest record from the first run.
            File.ReadAllText($"{path}.1").ShouldContain("\"message\":\"a\"");
        }
        finally { CleanupAll(path); }
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
