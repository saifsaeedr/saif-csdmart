using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart;

// Single writer to LOG_FILE, shared by both the generic ILogger pipeline
// (via FileLoggerProvider) and the per-request access log emitted by
// RequestLoggingMiddleware. Having one file handle + one lock avoids
// interleaved writes that two independent StreamWriters would produce.
//
// When `DmartSettings.LogFile` is empty the sink is "inactive" — all Write*
// calls are no-ops, so callers can depend on this type unconditionally.
// When LogFile is set the sink opens the file in append mode with AutoFlush
// so lines are durable without an explicit flush per message; format is
// always JSON lines (matches Python `.ljson.log`).
//
// Size-based rotation. When adding the current line would push file size
// past `LogMaxBytes`, the active stream is closed and one of three modes
// runs based on `LogBackupCount`:
//   < 0  — unlimited (default): scan the directory for existing
//          "{file}.<N>" archives, pick the next free index, rename current
//          → "{file}.<N+1>", reopen. Archives accumulate forever; oldest
//          has the lowest N, newest has the highest. Operators handle
//          retention out of band (logrotate, journald, external archival).
//   = 0  — truncate in place on rollover, no archives kept.
//   > 0  — Python parity (RotatingFileHandler.doRollover): shift
//          "{file}.{i}" → "{file}.{i+1}" from i=N-1 down to 1, rename
//          active → "{file}.1", reopen. "{file}.1" is always newest,
//          "{file}.N" is oldest.
// With `LogMaxBytes<=0` rotation is disabled entirely.
//
// Concurrency assumptions:
//   - Single-process: `LogFile` MUST NOT be shared across dmart processes.
//     Two processes rotating the same file race the rename/delete cascade
//     and can drop or duplicate archives. dmart deploys one `dmart serve`
//     per host, mirroring Python's single-handler model.
//   - In-process: rotation runs while holding `_lock`. Slow filesystem
//     renames (NFS, encrypted volumes) therefore stall every concurrent
//     write for the duration — acceptable for local-disk deployments,
//     surfaces as a wedge if logs are pointed at a network mount.
//
// Records are serialized with a hand-rolled Utf8JsonWriter walk so the
// writer stays AOT-safe — request/response bodies are captured as nested
// Dictionary<string, object?> / List<object?> trees, which the default
// `JsonSerializer.Serialize<object>` overload can't handle without
// reflection. This writer accepts string/bool/number/null leaves plus
// nested Dictionary and List containers.
public sealed class LogSink : IDisposable
{
    private FileStream? _stream;
    private readonly object _lock = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _backupCount;
    private readonly bool _active;
    private long _currentSize;
    // Latches the first time a rollover leaves _stream null (e.g. disk full,
    // permission flip mid-rotation). Subsequent writes silently no-op — we
    // only emit one stderr line so a runaway loop can't drown the operator.
    private bool _rollFailureReported;

    // UnsafeRelaxedJsonEscaping keeps non-ASCII content (Arabic, emoji) as-is
    // rather than escaping each char — readable logs, and matches Python's
    // ensure_ascii=False default.
    private static readonly JsonWriterOptions WriterOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };
    private static readonly byte[] Newline = new[] { (byte)'\n' };

    // Stable across rotation — rotation briefly nulls `_stream` while
    // shifting backup files, but the sink is still considered active because
    // the file was successfully opened at construction time.
    public bool IsActive => _active;

    public LogSink(IOptions<DmartSettings> settings)
    {
        var s = settings.Value;
        _path = s.LogFile ?? "";
        _maxBytes = s.LogMaxBytes;
        // Negatives (other than -1) collapse to -1 so the rollover branches
        // only need to test "< 0", "== 0", "> 0".
        _backupCount = s.LogBackupCount < 0 ? -1 : s.LogBackupCount;
        if (string.IsNullOrWhiteSpace(_path)) return;

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _currentSize = _stream.Length;
        _active = true;
    }

    // Generic ILogger → one JSONL line per event. Shape mirrors Python's
    // base JsonFormatter output so operators see a uniform schema whether
    // the line came from a plugin log or a request handler.
    //
    // Tagging: when the category identifies a plugin we prepend
    // `[<shortname>] ` to the message. Lets operators grep "[audit]" /
    // "[oodi_sync]" without parsing the JSON category. Skipped if the
    // message already starts with `[` to keep this idempotent across
    // sources that already self-tag (e.g. native plugins writing directly
    // to the file).
    public void WriteLog(string category, LogLevel level, string message)
    {
        if (!_active) return;
        var tag = PluginTagForCategory(category);
        if (tag is not null && !string.IsNullOrEmpty(message) && message[0] != '[')
            message = $"[{tag}] {message}";
        var record = new Dictionary<string, object?>
        {
            ["hostname"] = Environment.MachineName,
            ["time"] = TimeUtils.Now().ToString("yyyy-MM-dd HH:mm:ss,fff"),
            ["level"] = PythonLevel(level),
            ["category"] = category,
            ["message"] = message,
            ["thread"] = "MainThread",
            ["process"] = Environment.ProcessId,
        };
        WriteObject(record);
    }

    // Two recognized plugin-category shapes:
    //   1. `plugin.<shortname>[.sub]` — V4 native plugins via EmitPluginLog.
    //   2. `Dmart.Plugins.BuiltIn.<Class>Plugin` — built-in C# plugins using
    //      ILogger<TPlugin>. The class name is Pascal-cased; we convert to
    //      snake_case and strip the trailing `_plugin` to match the plugin's
    //      Shortname (e.g. "AuditPlugin" → "audit",
    //      "AdminNotificationSenderPlugin" → "admin_notification_sender").
    // Anything else returns null and the message is written unmodified.
    internal static string? PluginTagForCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return null;
        const string nativePrefix = "plugin.";
        if (category.StartsWith(nativePrefix, StringComparison.Ordinal))
        {
            var rest = category.AsSpan(nativePrefix.Length);
            var dot = rest.IndexOf('.');
            return (dot < 0 ? rest : rest[..dot]).ToString();
        }
        const string builtinPrefix = "Dmart.Plugins.BuiltIn.";
        if (category.StartsWith(builtinPrefix, StringComparison.Ordinal))
        {
            var name = category.Substring(builtinPrefix.Length);
            // Strip trailing "Plugin" if present.
            if (name.EndsWith("Plugin", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - "Plugin".Length);
            return PascalToSnake(name);
        }
        return null;
    }

    private static string PascalToSnake(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // Per-request access record. Already-built dictionary so the middleware
    // can attach custom props (request body, response body, headers map)
    // without a chain of overloads. Level is derived from status code so
    // grepping for "WARNING"/"ERROR" in the log still works.
    public void WriteAccessRecord(Dictionary<string, object?> record)
    {
        if (!_active) return;
        WriteObject(record);
    }

    private void WriteObject(Dictionary<string, object?> record)
    {
        using var buf = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(buf, WriterOpts))
        {
            WriteValue(writer, record);
        }
        buf.Write(Newline, 0, Newline.Length);
        var bytes = buf.ToArray();
        lock (_lock)
        {
            if (_stream is null) return;
            // Python parity: shouldRollover() checks tell()+len(msg) >= maxBytes.
            // When the next record would cross the limit we rotate before
            // writing, so each rotated file is at most maxBytes (last line
            // may push slightly over, matching Python's behavior).
            //
            // The `_currentSize > 0` guard avoids rotating an empty file —
            // happens when the very first write is itself larger than
            // maxBytes (only realistic with absurdly small limits in tests),
            // and would otherwise emit a useless empty `.1` archive.
            if (_maxBytes > 0 && _currentSize > 0 && _currentSize + bytes.Length > _maxBytes)
                Rollover();
            _stream!.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            _currentSize += bytes.Length;
        }
    }

    // Branches by _backupCount (see class doc-comment for the three modes).
    // Caller MUST hold _lock.
    private void Rollover()
    {
        if (_stream is null) return;
        _stream.Dispose();
        _stream = null;

        if (_backupCount < 0)
        {
            // Unlimited: append-numbering. Find the highest existing
            // "{file}.<N>" and rename current → "{file}.<N+1>". Survives
            // process restart because the scan reads disk state.
            var next = NextUnboundedArchiveIndex();
            if (File.Exists(_path)) File.Move(_path, $"{_path}.{next}");
        }
        else if (_backupCount > 0)
        {
            // Bounded shift (Python parity).
            for (var i = _backupCount - 1; i >= 1; i--)
            {
                var src = $"{_path}.{i}";
                var dst = $"{_path}.{i + 1}";
                if (!File.Exists(src)) continue;
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
            var firstBackup = $"{_path}.1";
            if (File.Exists(firstBackup)) File.Delete(firstBackup);
            if (File.Exists(_path)) File.Move(_path, firstBackup);
        }
        else
        {
            // backupCount=0 → no archive, just truncate in place.
            if (File.Exists(_path)) File.Delete(_path);
        }

        try
        {
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _currentSize = 0;
        }
        catch (Exception ex)
        {
            // Disk full, permission flip, parent directory removed mid-
            // rotation, etc. Logging is best-effort by design (the
            // `if (_stream is null) return;` guard at the WriteObject lock
            // keeps subsequent writes from throwing), but a silent failure
            // hides the moment the disk became unwritable. Emit ONE stderr
            // line so an operator notices, then go quiet.
            if (!_rollFailureReported)
            {
                _rollFailureReported = true;
                Console.Error.WriteLine(
                    $"[LogSink] rollover failed for '{_path}': {ex.GetType().Name}: {ex.Message}. "
                    + "Subsequent log writes will silently no-op until the next process restart.");
            }
        }
    }

    // Returns N+1 where N is the highest numeric suffix found among
    // "{filename}.<digits>" entries in the log directory. Starts at 1 when
    // no archives exist yet. We rescan on every rollover instead of caching
    // a counter so behavior is correct after restart and after operator
    // intervention (manual rename/delete of archives).
    //
    // Cost is O(n) in the number of files matching the prefix glob. With
    // the default 1 GiB rollover this is amortized to near-zero (rotations
    // happen on the order of hours, not seconds). Tiny LogMaxBytes values
    // (e.g. 1 MB in tests or accidentally in prod) compound this into
    // O(n²) total work over a process's lifetime — keep an eye on it if
    // you set LogMaxBytes much below 100 MB.
    private int NextUnboundedArchiveIndex()
    {
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var prefix = Path.GetFileName(_path) + ".";
        var max = 0;
        foreach (var entry in Directory.EnumerateFiles(dir, prefix + "*"))
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var suffix = name.Substring(prefix.Length);
            if (int.TryParse(suffix, out var n) && n > max) max = n;
        }
        return max + 1;
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case float f:
                w.WriteNumberValue(f);
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case Dictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (var (k, v) in dict)
                {
                    w.WritePropertyName(k);
                    WriteValue(w, v);
                }
                w.WriteEndObject();
                break;
            case IReadOnlyList<object?> list:
                w.WriteStartArray();
                foreach (var item in list) WriteValue(w, item);
                w.WriteEndArray();
                break;
            default:
                // Fallback — emit as string so the record is still valid JSON.
                w.WriteStringValue(value.ToString() ?? "");
                break;
        }
    }

    public static string PythonLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "DEBUG",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "INFO",
    };

    public void Dispose() => _stream?.Dispose();
}
