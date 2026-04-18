using System.Text;
using System.Text.Json;
using Dmart.Models.Enums;

namespace Dmart.Api.Mcp;

// Helpers for the MCP elicitation/create flow. The server-side initiates a
// question to the user (via SSE) and awaits the client's answer (arriving
// back on POST /mcp). Today we only use it for destructive confirms (delete);
// the same pattern works for any "are you sure?" gate.
//
// Why a helper rather than inlining in DeleteAsync:
//   - Pending-request bookkeeping (TaskCompletionSource, timeout, cleanup) is
//     non-trivial and doesn't belong in the tool body.
//   - Future destructive ops (bulk-delete, dangerous-patch) reuse this as-is.
public static class McpElicitation
{
    public enum Outcome
    {
        Accepted,
        Declined,
        Unsupported,  // client didn't advertise elicitation capability
    }

    // How long we wait for the client to answer. Claude Desktop's elicitation
    // UI surfaces immediately but the user might step away — 2 minutes is
    // long enough to grab coffee, short enough that the tool call doesn't
    // hang forever.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static async Task<Outcome> TryConfirmDeleteAsync(
        HttpContext http, string space, string subpath, string shortname,
        ResourceType rt, CancellationToken ct)
    {
        var session = ResolveSession(http);
        if (session is null || !session.ElicitationSupported)
            return Outcome.Unsupported;

        var message = $"Confirm delete of {space}{(subpath == "/" ? "" : subpath)}/{shortname} " +
                      $"({rt.ToString().ToLowerInvariant()})? This action cannot be undone.";
        var schema = """
            {
              "type": "object",
              "properties": {
                "confirm": {
                  "type": "boolean",
                  "description": "Whether to proceed with the deletion."
                }
              },
              "required": ["confirm"]
            }
            """;

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingElicitations[requestId] = tcs;

        var frame = BuildElicitationRequest(requestId, message, schema);
        if (!session.TryEnqueue(frame))
        {
            session.PendingElicitations.TryRemove(requestId, out _);
            return Outcome.Unsupported;  // no active SSE reader; can't ask
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var resultTask = tcs.Task;
            var completed = await Task.WhenAny(
                resultTask,
                Task.Delay(Timeout.Add(TimeSpan.FromSeconds(1)), cts.Token));
            if (completed != resultTask)
            {
                session.PendingElicitations.TryRemove(requestId, out _);
                return Outcome.Declined;  // treat timeout as decline
            }
            var result = await resultTask;
            return ReadAcceptDecision(result);
        }
        catch (OperationCanceledException)
        {
            session.PendingElicitations.TryRemove(requestId, out _);
            return Outcome.Declined;
        }
        catch
        {
            session.PendingElicitations.TryRemove(requestId, out _);
            return Outcome.Declined;
        }
    }

    // Parse the MCP elicitation response shape:
    //   { "action": "accept"|"decline"|"cancel", "content": { ... } }
    // We only accept on "action=accept" + confirm=true. Any other shape =
    // declined, including malformed responses.
    private static Outcome ReadAcceptDecision(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return Outcome.Declined;
        if (!result.TryGetProperty("action", out var act)
            || act.ValueKind != JsonValueKind.String) return Outcome.Declined;
        if (!string.Equals(act.GetString(), "accept", StringComparison.OrdinalIgnoreCase))
            return Outcome.Declined;

        if (!result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object) return Outcome.Declined;
        if (!content.TryGetProperty("confirm", out var conf)) return Outcome.Declined;
        return conf.ValueKind == JsonValueKind.True ? Outcome.Accepted : Outcome.Declined;
    }

    private static string BuildElicitationRequest(string requestId, string message, string schema)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("id", requestId);
            w.WriteString("method", "elicitation/create");
            w.WriteStartObject("params");
            w.WriteString("message", message);
            w.WritePropertyName("requestedSchema");
            using (var schemaDoc = JsonDocument.Parse(schema))
                schemaDoc.RootElement.WriteTo(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static McpSessionState? ResolveSession(HttpContext http)
    {
        var store = http.RequestServices.GetService(typeof(McpSessionStore)) as McpSessionStore;
        if (store is null) return null;
        var sessionId = http.Request.Headers["Mcp-Session-Id"].ToString();
        return string.IsNullOrEmpty(sessionId) ? null : store.Get(sessionId);
    }
}
