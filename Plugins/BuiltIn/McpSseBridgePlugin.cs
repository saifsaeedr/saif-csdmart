using System.Text;
using System.Text.Json;
using Dmart.Api.Mcp;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Plugins.BuiltIn;

// Bridges dmart's in-process event bus (IHookPlugin) into active MCP sessions'
// server→client SSE streams. Mirrors what RealtimeUpdatesNotifierPlugin does
// for WebSocket clients, except the channel format is MCP's:
//   - `notifications/resources/updated` with the canonical dmart:// URI
//     whenever an entry is created/updated/deleted.
//
// Fan-out policy: push the notification to every session whose authenticated
// user has read access. We delegate the read check to PermissionService rather
// than pre-filter by role — roles can change between login and event-fire, and
// the permission service is already what the tool handlers use.
//
// All sessions reliably miss events they started listening for after the fact
// (the outbox is a bounded channel, not a log). That matches how MCP clients
// treat notifications anyway — as hints to refresh, not ground truth.
public sealed class McpSseBridgePlugin(
    McpSessionStore sessions,
    PermissionService perms,
    ILogger<McpSseBridgePlugin> log) : IHookPlugin
{
    public string Shortname => "mcp_sse_bridge";

    public async Task HookAsync(Event e, CancellationToken ct = default)
    {
        // Only resource mutations are interesting to MCP clients. Ignore
        // login/logout/query events — they create noise and reveal nothing
        // actionable.
        if (e.ActionType != ActionType.Create
            && e.ActionType != ActionType.Update
            && e.ActionType != ActionType.Delete
            && e.ActionType != ActionType.ProgressTicket)
            return;

        if (string.IsNullOrEmpty(e.Shortname) || !e.ResourceType.HasValue)
            return;

        var sessionList = sessions.All().ToList();
        if (sessionList.Count == 0) return;

        var uri = BuildUri(e.SpaceName, e.Subpath, e.Shortname);
        var payload = BuildNotification(uri, e);
        var locator = new Locator(e.ResourceType.Value, e.SpaceName, e.Subpath, e.Shortname);

        // Per-session permission check: fan out only to sessions whose user
        // actually has read access to the mutated entry.
        foreach (var session in sessionList)
        {
            if (string.IsNullOrEmpty(session.UserShortname)) continue;
            bool allowed;
            try
            {
                allowed = await perms.CanReadAsync(session.UserShortname, locator, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "mcp_sse_bridge: CanRead failed for {Session}", session.Id);
                continue;
            }
            if (!allowed) continue;

            if (!session.TryEnqueue(payload))
                log.LogDebug("mcp_sse_bridge: outbox full for session {Session} — frame dropped",
                    session.Id);
        }
    }

    private static string BuildUri(string space, string subpath, string? shortname)
    {
        var prefix = subpath == "/" ? "" : subpath;
        return $"dmart://{space}{prefix}/{shortname}";
    }

    // `notifications/resources/updated` per MCP spec:
    //   { "method":"notifications/resources/updated", "params":{"uri":"..."} }
    // We also tuck an `action` + `owner` into params so clients that care can
    // render richer diffs without calling back into dmart.read.
    private static string BuildNotification(string uri, Event e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", "notifications/resources/updated");
            w.WriteStartObject("params");
            w.WriteString("uri", uri);
            w.WriteString("action", e.ActionType.ToString().ToLowerInvariant());
            w.WriteString("owner", e.UserShortname);
            if (e.ResourceType.HasValue)
                w.WriteString("resource_type", e.ResourceType.Value.ToString().ToLowerInvariant());
            if (!string.IsNullOrEmpty(e.SchemaShortname))
                w.WriteString("schema_shortname", e.SchemaShortname);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
