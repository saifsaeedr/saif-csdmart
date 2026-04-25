using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dmart.Middleware;

// Per-request structured access log — mirrors Python dmart's set_logging() in
// backend/main.py. Two modes:
//
//   * LOG_FILE unset → lightweight ILogger call ("HTTP GET /foo → 200 (5ms)
//     user=dmart cid=..."); the built-in console/json loggers format it.
//   * LOG_FILE set   → Python-parity JSON record ("Served request" with
//     props.request and props.response), written via LogSink. Request and
//     response bodies are captured (capped at 32 KB each, JSON only) with
//     secrets in bodies and headers redacted.
//
// Static assets under {cxb}/* and OPTIONS preflights are skipped in both
// modes to keep log volume proportional to real API traffic.
public static class RequestLoggingMiddleware
{
    private const int MaxBodyBytes = 32 * 1024;

    // Header names that never belong in a log (case-insensitive).
    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "set-cookie", "x-api-key",
    };

    // Body field names that carry secrets — redacted at any nesting level.
    private static readonly HashSet<string> RedactedBodyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "old_password", "new_password", "password_confirm",
        "jwt_secret", "database_password", "admin_password", "smpp_auth_key",
        "otp", "email_otp", "msisdn_otp", "code",
        "access_token", "refresh_token", "firebase_token", "auth_token", "token",
        "invitation",
        "apple_client_secret", "google_client_secret", "facebook_client_secret",
        "mail_password",
    };

    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var sink = ctx.RequestServices.GetService<LogSink>();
            var path = ctx.Request.Path.Value ?? "/";

            // Skip conditions apply in both modes.
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                await next();
                return;
            }
            if (path.StartsWith("/cxb/") && path != "/cxb/config.json")
            {
                await next();
                return;
            }

            if (sink is { IsActive: true })
                await LogWithBodyCapture(ctx, sink, next);
            else
                await LogLightweight(ctx, next);
        });
    }

    private static async Task LogLightweight(HttpContext ctx, Func<Task> next)
    {
        var sw = Stopwatch.StartNew();
        await next();
        sw.Stop();

        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Dmart.RequestLog");
        var status = ctx.Response.StatusCode;
        var user = ctx.ActorOrAnonymous();
        var correlationId = ctx.Response.Headers["X-Correlation-ID"].ToString();
        var durationMs = sw.ElapsedMilliseconds;

        if (status >= 500)
            log.LogError("HTTP {Method} {Path} → {Status} ({Duration}ms) user={User} cid={Cid}",
                ctx.Request.Method, ctx.Request.Path.Value, status, durationMs, user, correlationId);
        else if (status >= 400)
            log.LogWarning("HTTP {Method} {Path} → {Status} ({Duration}ms) user={User} cid={Cid}",
                ctx.Request.Method, ctx.Request.Path.Value, status, durationMs, user, correlationId);
        else
            log.LogInformation("HTTP {Method} {Path} → {Status} ({Duration}ms) user={User} cid={Cid}",
                ctx.Request.Method, ctx.Request.Path.Value, status, durationMs, user, correlationId);
    }

    private static async Task LogWithBodyCapture(HttpContext ctx, LogSink sink, Func<Task> next)
    {
        // --- capture request body (JSON only) ---
        object? requestBody = new Dictionary<string, object?>();
        if (HasJsonContent(ctx.Request.ContentType))
        {
            ctx.Request.EnableBuffering();
            var bodyBytes = await ReadBodyAsync(ctx.Request.Body, MaxBodyBytes, ctx.RequestAborted);
            ctx.Request.Body.Position = 0;
            requestBody = ParseJsonOrRaw(bodyBytes);
        }

        // --- intercept response body so we can capture the first MaxBodyBytes
        // for the log record, while writing through to the real stream. This
        // avoids buffering the entire response in memory (which doubles RAM for
        // large exports/CSV/binary payloads). ---
        var originalResponseBody = ctx.Response.Body;
        using var captureTee = new TeeStream(originalResponseBody, MaxBodyBytes);
        ctx.Response.Body = captureTee;

        var sw = Stopwatch.StartNew();
        Exception? captured = null;
        try { await next(); }
        catch (Exception ex) { captured = ex; }
        sw.Stop();

        ctx.Response.Body = originalResponseBody;

        object? responseBody = new Dictionary<string, object?>();
        if (HasJsonContent(ctx.Response.ContentType))
        {
            var bytes = captureTee.CapturedBytes;
            if (bytes.Length > 0)
                responseBody = ParseJsonOrRaw(bytes);
        }

        // Client-disconnect = the captured exception is a cancellation AND
        // RequestAborted has fired. Same heuristic WebSocketHandler and
        // McpEndpoint use for streaming endpoints; extended here to unary HTTP.
        // Treat as a benign info event: drop the `exception` decoration, mark
        // `client_aborted: true`, and don't re-throw to the global exception
        // handler (the client is gone — writing a 500 into a dead connection
        // just generates a second misleading ERROR line).
        var clientAborted = captured is OperationCanceledException
            && ctx.RequestAborted.IsCancellationRequested;

        var status = ctx.Response.StatusCode;
        var level = clientAborted ? LogLevel.Information
                  : status >= 500 ? LogLevel.Error
                  : status >= 400 ? LogLevel.Warning
                  : LogLevel.Information;
        var user = ctx.ActorOrAnonymous();
        var correlationId = ctx.Response.Headers["X-Correlation-ID"].ToString();

        var record = new Dictionary<string, object?>
        {
            ["hostname"] = Environment.MachineName,
            ["correlation_id"] = correlationId,
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss,fff"),
            ["level"] = LogSink.PythonLevel(level),
            ["message"] = "Served request",
            ["props"] = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                ["duration"] = sw.Elapsed.TotalSeconds,
                ["server"] = Environment.MachineName,
                ["process_id"] = Environment.ProcessId,
                ["user_shortname"] = user,
                ["request"] = new Dictionary<string, object?>
                {
                    ["url"] = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}{ctx.Request.Path}{ctx.Request.QueryString}",
                    ["verb"] = ctx.Request.Method,
                    ["path"] = ctx.Request.Path.Value ?? "",
                    ["query_params"] = ctx.Request.Query.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.ToString()),
                    ["headers"] = RedactHeaders(ctx.Request.Headers),
                    ["body"] = RedactBody(requestBody),
                },
                ["response"] = new Dictionary<string, object?>
                {
                    ["headers"] = RedactHeaders(ctx.Response.Headers),
                    ["http_status"] = status,
                    ["body"] = RedactBody(responseBody),
                },
            },
            ["thread"] = "MainThread",
            ["process"] = Environment.ProcessId,
        };

        if (clientAborted)
        {
            ((Dictionary<string, object?>)record["props"]!)["client_aborted"] = true;
        }
        else if (captured is not null)
        {
            ((Dictionary<string, object?>)record["props"]!)["exception"] =
                $"{captured.GetType().FullName}: {captured.Message}";
        }

        sink.WriteAccessRecord(record);

        if (captured is not null && !clientAborted) throw captured;
    }

    private static bool HasJsonContent(string? contentType) =>
        !string.IsNullOrEmpty(contentType)
        && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBodyAsync(Stream stream, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length >= max) break;
        }
        return ms.ToArray();
    }

    private static object? ParseJsonOrRaw(byte[] bytes)
    {
        if (bytes.Length == 0) return new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return JsonElementToObject(doc.RootElement);
        }
        catch
        {
            // Not valid JSON (or truncated mid-stream) — log it as a string
            // so the event still surfaces; easier to debug than a silent drop.
            return new Dictionary<string, object?>
            {
                ["_raw"] = Encoding.UTF8.GetString(bytes),
            };
        }
    }

    private static object? JsonElementToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var i) ? i : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.ToString(),
    };

    private static Dictionary<string, object?> RedactHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            result[h.Key] = RedactedHeaders.Contains(h.Key) ? "******" : (object?)h.Value.ToString();
        }
        return result;
    }

    private static object? RedactBody(object? body) => body switch
    {
        Dictionary<string, object?> d => RedactDict(d),
        List<object?> l => l.Select(RedactBody).ToList(),
        _ => body,
    };

    private static Dictionary<string, object?> RedactDict(Dictionary<string, object?> dict)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in dict)
        {
            result[k] = RedactedBodyFields.Contains(k)
                ? (v is null ? null : "******")
                : RedactBody(v);
        }
        return result;
    }

    // Writes through to the inner stream while capturing the first `capBytes`
    // bytes in memory for log inspection. Once the cap is reached, subsequent
    // writes go directly to the inner stream with zero copy overhead.
    private sealed class TeeStream(Stream inner, int capBytes) : Stream
    {
        private readonly MemoryStream _capture = new(Math.Min(capBytes, 4096));

        public byte[] CapturedBytes => _capture.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            var remaining = capBytes - (int)_capture.Length;
            if (remaining > 0)
                _capture.Write(buffer, offset, Math.Min(count, remaining));
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer, offset, count, ct);
            var remaining = capBytes - (int)_capture.Length;
            if (remaining > 0)
                _capture.Write(buffer, offset, Math.Min(count, remaining));
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            var remaining = capBytes - (int)_capture.Length;
            if (remaining > 0)
                _capture.Write(buffer.Span[..Math.Min(buffer.Length, remaining)]);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _capture.Dispose();
            base.Dispose(disposing);
        }
    }
}
