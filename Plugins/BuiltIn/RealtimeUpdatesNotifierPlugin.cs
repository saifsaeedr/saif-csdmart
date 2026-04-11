using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Plugins.BuiltIn;

// Port of dmart/backend/plugins/realtime_updates_notifier/plugin.py.
//
// For every matching event, compute the set of channels the event belongs to
// (walking subpath prefixes and crossing action/state/schema_shortname against
// __ALL__) and POST them to {WebsocketUrl}/broadcast-to-channels.
//
// If WebsocketUrl is not configured, the plugin is a no-op — matching Python's
// `if not settings.websocket_url: return`. This lets the config.json stay
// active without forcing every deployment to run a websocket bridge.
public sealed class RealtimeUpdatesNotifierPlugin(
    IOptions<DmartSettings> settings,
    IHttpClientFactory? clients,
    ILogger<RealtimeUpdatesNotifierPlugin> log) : IHookPlugin
{
    private const string AllMkw = "__ALL__";

    public string Shortname => "realtime_updates_notifier";

    public async Task HookAsync(Event e, CancellationToken ct = default)
    {
        var websocketUrl = settings.Value.WebsocketUrl;
        if (string.IsNullOrEmpty(websocketUrl)) return;
        if (clients is null) return;

        var state = e.Attributes.TryGetValue("state", out var s) ? s?.ToString() ?? AllMkw : AllMkw;
        var actionStr = JsonbHelpers.EnumMember(e.ActionType);
        var channels = new HashSet<string>(StringComparer.Ordinal);

        // Walk subpath prefixes: "a/b/c" → "/a", "/a/b", "/a/b/c". For each
        // prefix we add the 4 __ALL__ / schema / action / state combinations
        // Python builds.
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

        var payload = new RealtimeBroadcastBody(
            Type: "notification_subscription",
            Channels: channels.ToList(),
            Message: new RealtimeMessageBody(
                Title: "updated",
                Subpath: e.Subpath,
                Space: e.SpaceName,
                Shortname: e.Shortname,
                ActionType: actionStr,
                OwnerShortname: e.UserShortname));

        try
        {
            var http = clients.CreateClient("realtime_updates");
            var url = $"{websocketUrl.TrimEnd('/')}/broadcast-to-channels";
            using var body = new StringContent(
                JsonSerializer.Serialize(payload, DmartJsonContext.Default.RealtimeBroadcastBody),
                System.Text.Encoding.UTF8,
                "application/json");
            using var resp = await http.PostAsync(url, body, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "realtime_updates_notifier: POST failed");
        }
    }
}

// Serialized wire form for the broadcast POST body. Snake_case is handled by
// DmartJsonContext's top-level naming policy.
public sealed record RealtimeBroadcastBody(
    string Type,
    List<string> Channels,
    RealtimeMessageBody Message);

public sealed record RealtimeMessageBody(
    string Title,
    string Subpath,
    string Space,
    string? Shortname,
    string ActionType,
    string OwnerShortname);
