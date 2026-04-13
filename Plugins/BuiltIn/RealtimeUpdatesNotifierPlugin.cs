using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Services;

namespace Dmart.Plugins.BuiltIn;

// Port of dmart/backend/plugins/realtime_updates_notifier/plugin.py.
//
// For every matching event, compute the set of channels the event belongs to
// (walking subpath prefixes × action/state/schema_shortname × __ALL__) and
// broadcast directly to connected WebSocket clients via WsConnectionManager.
//
// In Python, this was an HTTP POST to a separate websocket process. In C# the
// WebSocket server runs in the same process, so we call the manager directly —
// no HTTP round-trip, no WebsocketUrl config needed.
public sealed class RealtimeUpdatesNotifierPlugin(
    WsConnectionManager wsMgr,
    ILogger<RealtimeUpdatesNotifierPlugin> log) : IHookPlugin
{
    private const string AllMkw = "__ALL__";

    public string Shortname => "realtime_updates_notifier";

    public async Task HookAsync(Event e, CancellationToken ct = default)
    {
        var state = e.Attributes.TryGetValue("state", out var s) ? s?.ToString() ?? AllMkw : AllMkw;
        var actionStr = JsonbHelpers.EnumMember(e.ActionType);
        var channels = new HashSet<string>(StringComparer.Ordinal);

        // Walk subpath prefixes: "a/b/c" → "/a", "/a/b", "/a/b/c". For each
        // prefix we add the 4 __ALL__ / schema / action / state combinations.
        var buffer = "";
        foreach (var part in e.Subpath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            buffer += part;
            if (!buffer.StartsWith('/')) buffer = "/" + buffer;

            channels.Add($"{e.SpaceName}:{buffer}:{AllMkw}:{actionStr}:{state}");
            channels.Add($"{e.SpaceName}:{buffer}:{AllMkw}:{AllMkw}:{state}");
            channels.Add($"{e.SpaceName}:{buffer}:{AllMkw}:{actionStr}:{AllMkw}");
            channels.Add($"{e.SpaceName}:{buffer}:{AllMkw}:{AllMkw}:{AllMkw}");

            if (!string.IsNullOrEmpty(e.SchemaShortname))
            {
                channels.Add($"{e.SpaceName}:{buffer}:{e.SchemaShortname}:{actionStr}:{state}");
                channels.Add($"{e.SpaceName}:{buffer}:{e.SchemaShortname}:{AllMkw}:{state}");
                channels.Add($"{e.SpaceName}:{buffer}:{e.SchemaShortname}:{actionStr}:{AllMkw}");
                channels.Add($"{e.SpaceName}:{buffer}:{e.SchemaShortname}:{AllMkw}:{AllMkw}");
            }

            buffer += "/";
        }

        // Broadcast directly to connected WebSocket clients — no HTTP round-trip.
        // Manual JSON to avoid anonymous type serialization (AOT-unsafe).
        var esc = (string? s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
        var message = $"{{\"type\":\"notification_subscription\",\"message\":{{\"title\":\"updated\",\"subpath\":\"{esc(e.Subpath)}\",\"space\":\"{esc(e.SpaceName)}\",\"shortname\":\"{esc(e.Shortname)}\",\"action_type\":\"{actionStr}\",\"owner_shortname\":\"{esc(e.UserShortname)}\"}}}}";

        foreach (var channel in channels)
        {
            try { await wsMgr.BroadcastToChannelAsync(channel, message); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "realtime_updates_notifier: broadcast to {Channel} failed", channel);
            }
        }
    }
}
