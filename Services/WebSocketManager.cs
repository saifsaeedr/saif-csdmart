using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Dmart.Services;

// Port of dmart/websocket.py::ConnectionManager. Tracks connected WebSocket
// clients by user shortname and manages channel subscriptions for real-time
// notifications. The realtime_updates_notifier plugin POSTs to
// /broadcast-to-channels which fans out to subscribed clients.
public sealed class WsConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _channels = new();

    public async Task ConnectAsync(WebSocket ws, string userShortname)
    {
        // Replace any existing connection for this user (Python does the same).
        if (_connections.TryRemove(userShortname, out var old))
        {
            try { await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); }
            catch { /* already closed */ }
        }
        _connections[userShortname] = ws;
    }

    public void Disconnect(string userShortname)
    {
        _connections.TryRemove(userShortname, out _);
        RemoveAllSubscriptions(userShortname);
    }

    public async Task<bool> SendMessageAsync(string userShortname, string message)
    {
        if (!_connections.TryGetValue(userShortname, out var ws)) return false;
        if (ws.State != WebSocketState.Open) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> BroadcastToChannelAsync(string channelName, string message)
    {
        if (!_channels.TryGetValue(channelName, out var users)) return false;
        var sent = false;
        foreach (var user in users)
            sent |= await SendMessageAsync(user, message);
        return sent;
    }

    public void Subscribe(string userShortname, string channelName)
    {
        RemoveAllSubscriptions(userShortname);
        var bag = _channels.GetOrAdd(channelName, _ => new ConcurrentBag<string>());
        bag.Add(userShortname);
    }

    public void RemoveAllSubscriptions(string userShortname)
    {
        foreach (var (_, users) in _channels)
        {
            // ConcurrentBag doesn't have Remove — rebuild without this user.
            // Acceptable because subscription changes are rare relative to messages.
            var filtered = new ConcurrentBag<string>(users.Where(u => u != userShortname));
            // No atomic swap on ConcurrentDictionary values — tolerable for this use case.
        }
        // Simpler approach: rebuild affected channels
        foreach (var key in _channels.Keys.ToArray())
        {
            if (_channels.TryGetValue(key, out var users))
            {
                var filtered = new ConcurrentBag<string>(users.Where(u => u != userShortname));
                _channels[key] = filtered;
            }
        }
    }

    // Python's generate_channel_name: "space:subpath:schema:action:state"
    public static string? GenerateChannelName(JsonElement msg)
    {
        if (!msg.TryGetProperty("space_name", out var sn) || !msg.TryGetProperty("subpath", out var sp))
            return null;
        var schema = msg.TryGetProperty("schema_shortname", out var ss) ? ss.GetString() ?? "__ALL__" : "__ALL__";
        var action = msg.TryGetProperty("action_type", out var at) ? at.GetString() ?? "__ALL__" : "__ALL__";
        var state = msg.TryGetProperty("ticket_state", out var ts) ? ts.GetString() ?? "__ALL__" : "__ALL__";
        return $"{sn.GetString()}:{sp.GetString()}:{schema}:{action}:{state}";
    }

    public int ConnectionCount => _connections.Count;
    public IReadOnlyDictionary<string, ConcurrentBag<string>> Channels => _channels;
}
