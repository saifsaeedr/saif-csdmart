using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.Models.Json;

namespace Dmart.Middleware;

// Intentional divergence from Python dmart: every API JSON response has its
// empty object properties dropped before being written to the wire. Python
// emits `{"tags": [], "displayname": {"en": ""}, "payload": {}}` because
// `model_dump(exclude_none=True)` only filters None. Clients of dmart
// consistently treat empty == absent, so we strip empties at the edge.
//
// What gets dropped (only as the value of an OBJECT property — never as an
// array element, which would shift positional indices):
//   - empty string ""
//   - empty array []
//   - empty object {}     (after recursive stripping of its own contents,
//                           so {a: {b: []}} → {a: {}} → drops a entirely)
//
// What stays:
//   - 0, 0.0, false       (meaningful primitives, never empty)
//   - null                (already stripped by DefaultIgnoreCondition.WhenWritingNull
//                           at serialize time, but we tolerate it here too)
//   - whitespace strings  (caller chose to send them)
//
// Implementation: buffer the response body, parse to JsonNode, walk + strip in
// place, write back. JsonNode is AOT-safe (no reflection). Non-JSON responses
// pass through unmodified. Parse failures fall back to writing the original
// buffer so we never break a working response.
public static class JsonStripEmptiesMiddleware
{
    public static IApplicationBuilder UseJsonStripEmpties(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var origBody = ctx.Response.Body;
            using var buffer = new MemoryStream();
            ctx.Response.Body = buffer;
            try
            {
                await next();

                buffer.Position = 0;
                var contentType = ctx.Response.ContentType ?? "";
                var isJson = contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

                if (buffer.Length > 0 && isJson)
                {
                    JsonNode? node = null;
                    try { node = JsonNode.Parse(buffer); }
                    catch (JsonException) { /* fall through to passthrough */ }

                    if (node is not null)
                    {
                        StripEmpties(node);
                        var bytes = Encoding.UTF8.GetBytes(
                            node.ToJsonString(DmartJsonContext.Default.Options));
                        // Let ASP.NET re-infer Content-Length (compression /
                        // chunked may have changed it). Setting to null also
                        // covers the case where the handler set the original
                        // pre-strip length.
                        ctx.Response.ContentLength = null;
                        await origBody.WriteAsync(bytes);
                        return;
                    }

                    buffer.Position = 0;
                }

                if (buffer.Length > 0)
                    await buffer.CopyToAsync(origBody);
            }
            finally
            {
                ctx.Response.Body = origBody;
            }
        });
    }

    // Recursive in-place strip. Object properties whose value is empty (after
    // recursing into the value first) are removed. Array elements are recursed
    // into but never removed.
    internal static void StripEmpties(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                // Snapshot keys — modifying obj during enumeration would throw.
                List<string>? toRemove = null;
                foreach (var kv in obj)
                {
                    if (kv.Value is not null) StripEmpties(kv.Value);
                    if (kv.Key == "payload") continue;
                    if (IsEmpty(kv.Value))
                        (toRemove ??= new List<string>()).Add(kv.Key);
                }
                if (toRemove is not null)
                    foreach (var k in toRemove) obj.Remove(k);
                break;

            case JsonArray arr:
                foreach (var elem in arr)
                    if (elem is not null) StripEmpties(elem);
                break;

            // JsonValue → leaf, no descent needed.
        }
    }

    private static bool IsEmpty(JsonNode? node)
    {
        if (node is null) return true;
        switch (node)
        {
            case JsonObject obj: return obj.Count == 0;
            case JsonArray arr: return arr.Count == 0;
            case JsonValue:
                return node.GetValueKind() == JsonValueKind.String
                    && node.GetValue<string>().Length == 0;
            default: return false;
        }
    }
}
