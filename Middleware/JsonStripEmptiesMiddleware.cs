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
// Implementation: a SniffingBodyStream wraps the response body and decides on
// the FIRST write, by inspecting Response.ContentType, whether to buffer (JSON,
// so we can strip) or stream straight through (everything else). Only buffered
// JSON is parsed to a JsonNode, walked + stripped in place, and written back.
// JsonNode is AOT-safe (no reflection). Non-JSON responses — a 50MB attachment
// download, CSV export, SPA asset, QR image — stream with zero buffering. Parse
// failures fall back to writing the original buffer so we never break a working
// response.
public static class JsonStripEmptiesMiddleware
{
    public static IApplicationBuilder UseJsonStripEmpties(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var origBody = ctx.Response.Body;
            var sniffing = new SniffingBodyStream(origBody, ctx.Response);
            ctx.Response.Body = sniffing;
            try
            {
                await next();

                // Passthrough (non-JSON) already wrote directly to origBody, and
                // a response with no body never decided — nothing to do.
                if (!sniffing.Buffered)
                    return;

                var buffer = sniffing.Buffer!;
                buffer.Position = 0;
                var contentType = ctx.Response.ContentType ?? "";
                var isJson = contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

                if (buffer.Length > 0 && isJson)
                {
                    JsonNode? node = null;
                    try { node = await JsonNode.ParseAsync(buffer, cancellationToken: ctx.RequestAborted); }
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
                sniffing.Buffer?.Dispose();
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
                    // Keys preserved even when empty:
                    //   * payload — Python emits {} for entries without bodies;
                    //     clients branch on schema_shortname inside it.
                    //   * relationships — Python parity (Meta.model_dump emits
                    //     []); the empty-vs-absent distinction matters here
                    //     because relationships is part of the documented
                    //     Meta shape, not an optional add-on. EntryMapper and
                    //     EntryToJsonNode both materialize this as [] when
                    //     unset; the middleware must not undo their work.
                    if (kv.Key is "payload" or "relationships") continue;
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

// Wraps the response body and decides, on the first write, whether to buffer
// (JSON — so the middleware can strip empties) or stream straight through
// (everything else). Streaming non-JSON avoids pulling large binary downloads
// fully into memory. A response that never writes a body never decides, so it
// is neither buffered nor flushed.
internal sealed class SniffingBodyStream : Stream
{
    // The wrapped stream is the original Response.Body, owned by ASP.NET Core —
    // its lifetime ends with the response, not with this wrapper. The middleware
    // restores ctx.Response.Body in a finally and never disposes this wrapper, so
    // disposing _inner here would be wrong (it could close the connection stream).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "Audited: _inner is the caller-owned Response.Body; this wrapper must not dispose it.")]
    private readonly Stream _inner;
    private readonly Microsoft.AspNetCore.Http.HttpResponse _response;
    private MemoryStream? _buffer;
    private bool _decided;
    private bool _passthrough;

    public SniffingBodyStream(Stream inner, Microsoft.AspNetCore.Http.HttpResponse response)
    {
        _inner = inner;
        _response = response;
    }

    // True once a write has happened AND the decision was to buffer. The
    // middleware reads this after next() to know whether to post-process.
    public bool Buffered => _decided && !_passthrough;
    public MemoryStream? Buffer => _buffer;

    private Stream Target()
    {
        if (!_decided)
        {
            _decided = true;
            var ct = _response.ContentType ?? "";
            // JSON → buffer so empties can be stripped. Null/empty content-type
            // → buffer too: it's the today-equivalent safe default (a handler
            // that writes before setting the type still gets stripped if the
            // body turns out to be JSON; non-JSON falls back to a verbatim
            // copy after a failed parse).
            var bufferIt = ct.Length == 0
                || ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
            _passthrough = !bufferIt;
            if (bufferIt) _buffer = new MemoryStream();
        }
        return _passthrough ? _inner : _buffer!;
    }

    public override bool CanWrite => true;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => Target().Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Target().WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => Target().WriteAsync(buffer, cancellationToken);

    // Only meaningful in passthrough mode; buffered output is flushed by the
    // middleware after stripping. Flushing before any write is a no-op.
    public override void Flush()
    {
        if (_decided && _passthrough) _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
        => (_decided && _passthrough) ? _inner.FlushAsync(cancellationToken) : Task.CompletedTask;
}
