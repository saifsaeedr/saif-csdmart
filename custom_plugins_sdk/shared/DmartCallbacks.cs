using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// SDK header version: V4 (Log callback). Tracks
// NativePluginCallbacks.CurrentVersion in lock-step.
namespace Dmart.Sdk;

// C# view of the DmartCallbacks struct dmart hands each native plugin via
// `init(const DmartCallbacks*)`. Layout MUST match the host definition in
// Plugins/Native/NativePluginCallbacks.cs — append fields only, never
// reorder.
//
// The trailing `Version` field is the canonical way to detect host
// capability level at runtime: a plugin built against this header reads
// _cb.Version and branches on it. Old plugins that don't know about a
// field simply ignore it; new plugins that read a field absent on an
// older host see whatever uninitialized memory the smaller host struct
// didn't write — so always check Version before using fields appended
// in versions newer than your build target.
//
// Usage in a plugin:
//
//     private static DmartCallbacks _cb;
//     private static bool _cbReady;
//
//     [UnmanagedCallersOnly(EntryPoint = "init")]
//     public static void Init(IntPtr cbsPtr)
//     {
//         _cb = Marshal.PtrToStructure<DmartCallbacks>(cbsPtr);
//         _cbReady = true;
//     }
//
//     [UnmanagedCallersOnly(EntryPoint = "hook")]
//     public static IntPtr Hook(IntPtr eventJsonPtr)
//     {
//         if (_cbReady) {
//             var user = DmartSdk.LoadUser(_cb, "dmart"); // returns JSON string
//             DmartSdk.SendEmail(_cb, "foo@example.com", "subject", "<p>body</p>");
//         }
//         ...
//     }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DmartCallbacks
{
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, byte*> LoadEntry;
    public delegate* unmanaged[Cdecl]<byte*, byte*> LoadUser;
    public delegate* unmanaged[Cdecl]<byte*, int> SaveEntry;
    public delegate* unmanaged[Cdecl]<byte*, int> UpdateUser;
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, int> SendEmail;
    public delegate* unmanaged[Cdecl]<byte*, byte*, int> WsBroadcast;
    public delegate* unmanaged[Cdecl]<byte*, void> DmartFree;
    // Generic Query — same shape as POST /managed/query body. By default
    // runs as the user that triggered the hook (user permissions honored);
    // include "as_actor" in the JSON (string to impersonate, null for
    // system / no ACL) to override.
    public delegate* unmanaged[Cdecl]<byte*, byte*> Query;
    // Media-attachment bytes, by (space, subpath, shortname). The 4th arg
    // is an int* the host writes the byte count to. Returned pointer must
    // be released via DmartFree. null when missing or no media column.
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, int*, byte*> GetMediaAttachment;

    // Capability-level marker. Bumped whenever fields are appended.
    //   1 — initial release (LoadEntry…DmartFree)
    //   2 — Query, GetMediaAttachment appended; Query was ACL-free
    //   3 — Query honors caller's actor by default; "as_actor" override
    //   4 — Log appended (plugin → ILogger pipeline)
    public int Version;

    // Plugin → host structured logging. Routes the message through dmart's
    // ILogger pipeline so it lands in the JSONL log file (when configured)
    // and honors the operator's log-level filters. Args:
    //   level    — Microsoft.Extensions.Logging.LogLevel int (0 Trace … 5 Critical)
    //   category — UTF-8 NUL-terminated; appended to "plugin.<shortname>"
    //              by the host. NULL means "use default (no subcategory)".
    //   message  — UTF-8 NUL-terminated, required.
    // Returns void; logging never fails to the plugin.
    // APPENDED in V4 — check Version >= 4 before calling, or use the
    // DmartSdk.Log* wrappers below which fall back to stderr.
    public delegate* unmanaged[Cdecl]<int, byte*, byte*, void> Log;
}

// Ergonomic wrappers that handle UTF-8 marshaling, null pointers, and
// freeing the returned string. Every method is self-contained so a plugin
// author can drop this single file into their project.
public static unsafe class DmartSdk
{
    public static string? LoadEntry(in DmartCallbacks cb, string space, string subpath,
        string shortname, string? resourceType = null)
    {
        if (cb.LoadEntry == null) return null;
        var spaceBuf = StringToUtf8(space);
        var subpathBuf = StringToUtf8(subpath);
        var shortnameBuf = StringToUtf8(shortname);
        var rtBuf = resourceType is null ? null : StringToUtf8(resourceType);
        try
        {
            var resultPtr = cb.LoadEntry(spaceBuf, subpathBuf, shortnameBuf, rtBuf);
            return TakeAndFree(cb, resultPtr);
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)spaceBuf);
            Marshal.FreeHGlobal((IntPtr)subpathBuf);
            Marshal.FreeHGlobal((IntPtr)shortnameBuf);
            if (rtBuf != null) Marshal.FreeHGlobal((IntPtr)rtBuf);
        }
    }

    public static string? LoadUser(in DmartCallbacks cb, string shortname)
    {
        if (cb.LoadUser == null) return null;
        var buf = StringToUtf8(shortname);
        try
        {
            var resultPtr = cb.LoadUser(buf);
            return TakeAndFree(cb, resultPtr);
        }
        finally { Marshal.FreeHGlobal((IntPtr)buf); }
    }

    public static int SaveEntry(in DmartCallbacks cb, string entryJson)
    {
        if (cb.SaveEntry == null) return -1;
        var buf = StringToUtf8(entryJson);
        try { return cb.SaveEntry(buf); }
        finally { Marshal.FreeHGlobal((IntPtr)buf); }
    }

    public static int UpdateUser(in DmartCallbacks cb, string userJson)
    {
        if (cb.UpdateUser == null) return -1;
        var buf = StringToUtf8(userJson);
        try { return cb.UpdateUser(buf); }
        finally { Marshal.FreeHGlobal((IntPtr)buf); }
    }

    public static int SendEmail(in DmartCallbacks cb, string to, string subject, string htmlBody)
    {
        if (cb.SendEmail == null) return -1;
        var toBuf = StringToUtf8(to);
        var subBuf = StringToUtf8(subject);
        var bodyBuf = StringToUtf8(htmlBody);
        try { return cb.SendEmail(toBuf, subBuf, bodyBuf); }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)toBuf);
            Marshal.FreeHGlobal((IntPtr)subBuf);
            Marshal.FreeHGlobal((IntPtr)bodyBuf);
        }
    }

    public static int WsBroadcast(in DmartCallbacks cb, string channel, string message)
    {
        if (cb.WsBroadcast == null) return -1;
        var chBuf = StringToUtf8(channel);
        var msgBuf = StringToUtf8(message);
        try { return cb.WsBroadcast(chBuf, msgBuf); }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)chBuf);
            Marshal.FreeHGlobal((IntPtr)msgBuf);
        }
    }

    // Run a /managed/query-shaped JSON body through the host and get the
    // Response JSON back. By default the query runs as the user whose
    // action triggered the hook — i.e. user permissions are honored. To
    // override the actor (impersonate another user, or bypass ACLs as the
    // system), use QueryAs.
    public static string? Query(in DmartCallbacks cb, string queryJson)
    {
        if (cb.Query == null) return null;
        var buf = StringToUtf8(queryJson);
        try { return TakeAndFree(cb, cb.Query(buf)); }
        finally { Marshal.FreeHGlobal((IntPtr)buf); }
    }

    // Same as Query, but explicitly run as the given actor:
    //   - asActor = "username" → impersonate that user
    //   - asActor = null       → run with no actor (system / no ACL)
    // Injects/overrides the "as_actor" field in the JSON body, then
    // delegates to Query.
    //
    // NOTE: this allocates a JsonDocument, MemoryStream, Utf8JsonWriter,
    // and a final string per call to rewrite the JSON. Cheap enough for
    // an action triggered by a hook, but don't loop on it.
    public static string? QueryAs(in DmartCallbacks cb, string queryJson, string? asActor)
    {
        if (cb.Query == null) return null;
        var rewritten = BuildQueryJsonWithActor(queryJson, asActor);
        if (rewritten is null) return null;
        return Query(in cb, rewritten);
    }

    // Pure JSON-rewrite helper — no unsafe, no callbacks. Drops any
    // existing "as_actor" key and writes the requested value (a string
    // to impersonate, JSON null to bypass ACLs). Returns null if the
    // input isn't a JSON object. Public so plugin authors can stage
    // request bodies before calling Query directly.
    public static string? BuildQueryJsonWithActor(string queryJson, string? asActor)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(queryJson);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("as_actor")) continue;
                prop.WriteTo(writer);
            }
            if (asActor is null) writer.WriteNull("as_actor");
            else writer.WriteString("as_actor", asActor);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Read the raw `media` BYTEA for an attachment by (space, subpath,
    // shortname). null when the attachment is missing or has no media.
    // The returned byte[] is a managed copy — the unmanaged buffer the
    // host returned is released via DmartFree before this method returns.
    public static byte[]? GetMediaAttachment(in DmartCallbacks cb, string space, string subpath, string shortname)
    {
        if (cb.GetMediaAttachment == null) return null;
        var spBuf = StringToUtf8(space);
        var subBuf = StringToUtf8(subpath);
        var snBuf = StringToUtf8(shortname);
        try
        {
            int len = 0;
            var ptr = cb.GetMediaAttachment(spBuf, subBuf, snBuf, &len);
            if (ptr == null || len <= 0) return null;
            try
            {
                var bytes = new byte[len];
                Marshal.Copy((IntPtr)ptr, bytes, 0, len);
                return bytes;
            }
            finally { if (cb.DmartFree != null) cb.DmartFree(ptr); }
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)spBuf);
            Marshal.FreeHGlobal((IntPtr)subBuf);
            Marshal.FreeHGlobal((IntPtr)snBuf);
        }
    }

    // ========================================================================
    // Logging
    // ========================================================================

    // Match Microsoft.Extensions.Logging.LogLevel int values exactly.
    public const int LogLevelTrace = 0;
    public const int LogLevelDebug = 1;
    public const int LogLevelInfo = 2;
    public const int LogLevelWarn = 3;
    public const int LogLevelError = 4;
    public const int LogLevelCritical = 5;

    // Plugins now own their log file: SetShortname opens
    // `$DMART_PLUGIN_LOG_DIR/<shortname>.ljson.log` (append) and every Log*
    // call writes a JSON line directly to it. No round-trip through the
    // host's V4 cb.Log callback — the cb argument is retained for source
    // compatibility with V4 plugins, but its Log field is not invoked.
    //
    // When the env var is unset, the file can't be opened, or SetShortname
    // hasn't been called, Log* falls back to stderr so plugins never crash
    // on a logging call.
    //
    // SECURITY: the file-write path does NOT redact secrets. Plugin authors
    // are responsible for keeping PII, passwords, tokens, OTPs, and session
    // cookies out of the message text. Log identifiers (user shortname,
    // request id) instead of raw credentials.

    // Allowlist for the shortname segment that goes into the log file name.
    // Lowercase letter or digit lead, then letters/digits/underscores/hyphens.
    // Rejects "..", "/", "\", "..\", etc. — anything that would let a
    // malicious or buggy plugin write outside the configured log dir.
    private static readonly Regex SafeShortname = new(
        @"^[a-z0-9][a-z0-9_-]{0,62}$", RegexOptions.Compiled);

    private static readonly object _logLock = new();
    private static FileStream? _logStream;
    private static string? _shortname;

    // Absolute path of the currently open plugin log file, or null when
    // file logging is inactive (env unset, SetShortname not called, name
    // rejected, or open failed). Tests rely on this to discover the path.
    public static string? LogPath { get; private set; }

    // Register this plugin's shortname. Opens
    // `$DMART_PLUGIN_LOG_DIR/<shortname>.ljson.log` for append. Calling it
    // again closes the prior stream and reopens against the new shortname.
    // Safe to call from `init` even when the host hasn't set the env var —
    // LogPath simply stays null and Log* writes go to stderr.
    public static void SetShortname(string shortname)
    {
        lock (_logLock)
        {
            _logStream?.Dispose();
            _logStream = null;
            _shortname = null;
            LogPath = null;

            if (string.IsNullOrEmpty(shortname)) return;
            if (!SafeShortname.IsMatch(shortname)) return;

            var dir = Environment.GetEnvironmentVariable("DMART_PLUGIN_LOG_DIR");
            if (string.IsNullOrEmpty(dir)) return;

            var path = Path.Combine(dir, $"{shortname}.ljson.log");
            try
            {
                Directory.CreateDirectory(dir);
                _logStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _shortname = shortname;
                LogPath = path;
            }
            catch
            {
                _logStream?.Dispose();
                _logStream = null;
                _shortname = null;
                LogPath = null;
            }
        }
    }

    public static void Log(in DmartCallbacks cb, int level, string? category, string message)
    {
        lock (_logLock)
        {
            if (_logStream is not null)
            {
                try
                {
                    WritePluginLogLine(_logStream, level, category, message);
                    return;
                }
                catch
                {
                    // Disk full, IO failure: fall through to stderr so the
                    // plugin keeps running. Don't drop the stream — a
                    // transient ENOSPC may clear and subsequent writes
                    // could succeed.
                }
            }
        }

        // Stderr fallback. Format mirrors the prior V4 SDK so log scrapers
        // built against older releases keep working.
        Console.Error.WriteLine(
            $"[{LevelName(level)}]"
            + (string.IsNullOrEmpty(category) ? "" : $" [{category}]")
            + $" {message}");
    }

    private static void WritePluginLogLine(FileStream stream, int level, string? category, string message)
    {
        using var ms = new MemoryStream(message.Length + 96);
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("time", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("level", LevelName(level));
            if (!string.IsNullOrEmpty(category)) w.WriteString("category", category);
            w.WriteString("message", message);
            w.WriteNumber("process", Environment.ProcessId);
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
        var bytes = ms.GetBuffer();
        stream.Write(bytes, 0, (int)ms.Length);
        stream.Flush();
    }

    public static void LogTrace(in DmartCallbacks cb, string message, string? category = null)
        => Log(in cb, LogLevelTrace, category, message);
    public static void LogDebug(in DmartCallbacks cb, string message, string? category = null)
        => Log(in cb, LogLevelDebug, category, message);
    public static void LogInfo(in DmartCallbacks cb, string message, string? category = null)
        => Log(in cb, LogLevelInfo, category, message);
    public static void LogWarn(in DmartCallbacks cb, string message, string? category = null)
        => Log(in cb, LogLevelWarn, category, message);
    public static void LogError(in DmartCallbacks cb, string message, string? category = null)
        => Log(in cb, LogLevelError, category, message);
    public static void LogCritical(in DmartCallbacks cb, string message, string? category = null)
        => Log(in cb, LogLevelCritical, category, message);

    private static string LevelName(int level) => level switch
    {
        LogLevelTrace => "TRACE",
        LogLevelDebug => "DEBUG",
        LogLevelInfo => "INFO",
        LogLevelWarn => "WARN",
        LogLevelError => "ERROR",
        LogLevelCritical => "CRITICAL",
        _ => "INFO",
    };

    // ========================================================================
    // Helpers
    // ========================================================================

    private static byte* StringToUtf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return (byte*)ptr;
    }

    private static string? TakeAndFree(in DmartCallbacks cb, byte* ptr)
    {
        if (ptr == null) return null;
        try { return Marshal.PtrToStringUTF8((IntPtr)ptr); }
        finally { if (cb.DmartFree != null) cb.DmartFree(ptr); }
    }
}
