using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Dmart;

// Small AOT-safe JSON helpers reused across the codebase. Kept reflection-free
// so they compile cleanly under `PublishAot=true`.
internal static class JsonUtil
{
    // UnsafeRelaxedJsonEscaping mirrors LogSink's default: non-ASCII (Arabic,
    // emoji, …) flows through without \uXXXX escaping so plugin configs
    // stay human-readable in the log.
    private static readonly JsonWriterOptions CompactOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    // Strip whitespace from a JSON buffer so it fits cleanly inside a
    // single-line structured log message (no embedded \n escapes). Falls
    // back to the raw text if the buffer doesn't parse as JSON — callers
    // logging user-supplied content shouldn't fail just because the input
    // was malformed.
    public static string Compact(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            using var ms = new MemoryStream(bytes.Length);
            using (var w = new Utf8JsonWriter(ms, CompactOpts))
                doc.WriteTo(w);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public static string Compact(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream(Encoding.UTF8.GetByteCount(json));
            using (var w = new Utf8JsonWriter(ms, CompactOpts))
                doc.WriteTo(w);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json;
        }
    }
}
