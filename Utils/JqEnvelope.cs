using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;

namespace Dmart.Utils;

// Applies a top-level jq_filter to a query Response and writes the result to
// the HTTP stream.
//
// Semantics: filter is written per-record (e.g., `{shortname, subpath}`), the
// server wraps it as `map(<filter>)`, pipes just the records array through
// `jq -c`, and slots jq's stdout back into the `records` field of the
// envelope. `status`, `attributes`, `error` are preserved. On validation /
// runtime failures, returns a normal Response.Fail envelope — only successful
// output rides in records[].
//
// Why records-only (not the whole Response envelope): user-authored filters
// are written against a single Record's shape, which is the natural mental
// model. This also matches the join-sub-query jq path in QueryService, which
// has always mapped per-record via `map( [ <filter> ] )`.
public static class JqEnvelope
{
    public static async Task WriteAsync(
        HttpResponse http, Response response, string filter, int timeoutSec, CancellationToken ct)
    {
        http.ContentType = "application/json; charset=utf-8";

        // Short-circuit: failed response or no records → serialize as-is.
        // (Also covers Response.Fail(...) envelopes from callers.)
        if (response.Status != Status.Success || response.Records is null)
        {
            await JsonSerializer.SerializeAsync(http.Body, response, DmartJsonContext.Default.Response, ct);
            return;
        }

        if (!JqRunner.ValidateFilter(filter, out _))
        {
            await WriteFailAsync(http, InternalErrorCode.JQ_ERROR,
                "jq_filter validation failed", ct);
            return;
        }

        var inputBytes = SerializeRecordsAsArray(response.Records);
        // `map(<filter>)` applies the user's filter to each Record and collects
        // into an array. User writes the filter as if it sees one Record; jq
        // iterates and collects.
        var wrapped = $"map({filter})";
        var jq = await JqRunner.RunRawAsync(wrapped, inputBytes, timeoutSec, ct);

        switch (jq.Failure)
        {
            case JqRunner.FailureKind.Timeout:
                await WriteFailAsync(http, InternalErrorCode.JQ_TIMEOUT,
                    "jq filter took too long to execute", ct);
                return;
            case JqRunner.FailureKind.JqMissing:
                await WriteFailAsync(http, InternalErrorCode.JQ_ERROR,
                    "jq binary not available on this dmart deployment", ct);
                return;
            case JqRunner.FailureKind.Invalid:
                await WriteFailAsync(http, InternalErrorCode.JQ_ERROR,
                    "jq_filter validation failed", ct);
                return;
            case JqRunner.FailureKind.JqError:
                // Surface jq's stderr so the filter author can debug their
                // own expression. Not a security concern — it's the caller's
                // filter, not dmart internals.
                await WriteFailAsync(http, InternalErrorCode.JQ_ERROR,
                    $"jq filter failed: {(jq.Stderr ?? "unknown error").Trim()}", ct);
                return;
        }

        await WriteEnvelopeAsync(http, response, jq.StdoutBytes ?? Array.Empty<byte>(), ct);
    }

    // Build the full Response envelope with raw jq output as the `records`
    // field value. `status`, `attributes`, `error` go through the source-gen
    // serializers; `records` is written via WriteRawValue.
    private static async Task WriteEnvelopeAsync(
        HttpResponse http, Response response, byte[] jqStdout, CancellationToken ct)
    {
        await using var writer = new Utf8JsonWriter(http.Body);
        writer.WriteStartObject();

        // status
        writer.WritePropertyName("status");
        JsonSerializer.Serialize(writer, response.Status, DmartJsonContext.Default.Status);

        // error (only when non-null; matches DefaultIgnoreCondition.WhenWritingNull)
        if (response.Error is not null)
        {
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, response.Error, DmartJsonContext.Default.Error);
        }

        // records: raw jq output. jq emits either a single JSON array (from
        // `map(...)`) or JSONL. `map()` on success yields a single array, but
        // trim trailing newline defensively before writing it raw.
        writer.WritePropertyName("records");
        var trimmed = TrimTrailingWhitespace(jqStdout);
        if (trimmed.Length == 0)
            writer.WriteNullValue();
        else
            writer.WriteRawValue(trimmed, skipInputValidation: false);

        // attributes
        if (response.Attributes is not null)
        {
            writer.WritePropertyName("attributes");
            JsonSerializer.Serialize(writer, response.Attributes,
                DmartJsonContext.Default.DictionaryStringObject);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private static async Task WriteFailAsync(HttpResponse http, int code, string message, CancellationToken ct)
    {
        var resp = Response.Fail(code, message, ErrorTypes.Request);
        await JsonSerializer.SerializeAsync(http.Body, resp, DmartJsonContext.Default.Response, ct);
    }

    // Serialize records as a JSON array using the source-gen Record serializer,
    // so the byte-for-byte shape fed into jq matches dmart's wire format.
    private static byte[] SerializeRecordsAsArray(List<Record> records)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var rec in records)
                JsonSerializer.Serialize(writer, rec, DmartJsonContext.Default.Record);
            writer.WriteEndArray();
        }
        return ms.ToArray();
    }

    private static ReadOnlySpan<byte> TrimTrailingWhitespace(byte[] bytes)
    {
        var end = bytes.Length;
        while (end > 0)
        {
            var b = bytes[end - 1];
            if (b == (byte)' ' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\t')
                end--;
            else break;
        }
        return new ReadOnlySpan<byte>(bytes, 0, end);
    }
}
