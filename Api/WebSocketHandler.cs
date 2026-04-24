using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api;

// Port of dmart/websocket.py — WebSocket endpoint + HTTP push/broadcast APIs.
//
// Endpoints:
//   GET  /ws?token=<jwt>             WebSocket upgrade with JWT auth
//   POST /send-message/{user}        Push a message to a specific user
//   POST /broadcast-to-channels      Broadcast to all subscribers of channels
//   GET  /ws-info                     List connected clients + channels
//
// The realtime_updates_notifier plugin POSTs to /broadcast-to-channels after
// every CRUD event, and CXB clients subscribe via the WebSocket connection.
public static class WebSocketHandler
{
    public static void MapWebSocket(this WebApplication app)
    {
        app.UseWebSockets();

        // GET /ws?token=<jwt> — WebSocket endpoint with JWT auth.
        app.Map("/ws", async (HttpContext ctx, WsConnectionManager mgr, JwtIssuer jwt) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("WebSocket upgrade required");
                return;
            }

            // Authenticate via query string token (Python: ?token=...)
            var token = ctx.Request.Query["token"].ToString();
            string? userShortname = null;
            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    var principal = jwt.Validate(token);
                    userShortname = principal?.Identity?.Name;
                }
                catch { /* invalid token */ }
            }
            // Also try cookie (CXB sends auth_token cookie)
            if (string.IsNullOrEmpty(userShortname))
            {
                var cookieToken = ctx.Request.Cookies["auth_token"];
                if (!string.IsNullOrEmpty(cookieToken))
                {
                    try
                    {
                        var principal = jwt.Validate(cookieToken);
                        userShortname = principal?.Identity?.Name;
                    }
                    catch { /* invalid cookie */ }
                }
            }

            if (string.IsNullOrEmpty(userShortname))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("Invalid token");
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await mgr.ConnectAsync(ws, userShortname);

            // Send connection success (matches Python's connection_response).
            await mgr.SendMessageAsync(userShortname,
                "{\"type\":\"connection_response\",\"message\":{\"status\":\"success\"}}");

            // Read loop — handle subscribe/unsubscribe messages.
            // Use ctx.RequestAborted so the loop stops on server shutdown.
            const int maxMessageSize = 64 * 1024; // 64 KB cap
            var buffer = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    // Accumulate fragments until EndOfMessage to handle
                    // messages larger than the 4 KB receive buffer.
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                        if (ms.Length + result.Count > maxMessageSize)
                        {
                            // Message too large — discard and close.
                            try { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", ctx.RequestAborted); }
                            catch { /* best-effort */ }
                            break;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var msg = doc.RootElement;
                        var msgType = msg.TryGetProperty("type", out var t) ? t.GetString() : null;

                        if (msgType == "notification_subscription")
                        {
                            var channel = WsConnectionManager.GenerateChannelName(msg);
                            if (channel is not null)
                            {
                                mgr.Subscribe(userShortname, channel);
                                await mgr.SendMessageAsync(userShortname,
                                    "{\"type\":\"notification_subscription\",\"message\":{\"status\":\"success\"}}");
                            }
                        }
                        else if (msgType == "notification_unsubscribe")
                        {
                            mgr.RemoveAllSubscriptions(userShortname);
                            await mgr.SendMessageAsync(userShortname,
                                "{\"type\":\"notification_unsubscribe\",\"message\":{\"status\":\"success\"}}");
                        }
                    }
                    catch { /* malformed message — ignore */ }
                }
            }
            catch (OperationCanceledException) { /* server shutdown or client abort */ }
            catch { /* client disconnected */ }
            finally
            {
                mgr.Disconnect(userShortname);
                if (ws.State == WebSocketState.Open)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                    catch { /* already closed */ }
                }
            }
        });

        // POST /send-message/{user_shortname} — push to a specific user.
        // Used by plugins (local_notification) to notify a single user.
        // Authenticated: plugin-to-server calls need a service token.
        app.MapPost("/send-message/{user_shortname}", async (
            string user_shortname, HttpRequest req, WsConnectionManager mgr) =>
        {
            var body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.JsonElement);
            var formatted = BuildWsMessageJson(body);
            var sent = await mgr.SendMessageAsync(user_shortname, formatted);
            return Results.Text(BuildSendResult(sent), "application/json");
        }).WithTags("WebSocket").RequireAuthorization();

        // POST /broadcast-to-channels — broadcast to subscribed clients.
        // Used by realtime_updates_notifier plugin after CRUD events.
        app.MapPost("/broadcast-to-channels", async (HttpRequest req, WsConnectionManager mgr) =>
        {
            var body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.JsonElement);
            var formatted = BuildWsMessageJson(body);

            var sent = false;
            if (body.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array)
            {
                foreach (var ch in channels.EnumerateArray())
                {
                    var channelName = ch.GetString();
                    if (channelName is not null)
                        sent |= await mgr.BroadcastToChannelAsync(channelName, formatted);
                }
            }
            return Results.Text(BuildSendResult(sent), "application/json");
        }).WithTags("WebSocket").RequireAuthorization();

        // GET /ws-info — list connected clients + channels (admin debugging).
        app.MapGet("/ws-info", (WsConnectionManager mgr) =>
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("status", "success");
                writer.WriteStartObject("data");
                writer.WriteNumber("connected_clients", mgr.ConnectionCount);
                writer.WriteStartObject("channels");
                foreach (var kv in mgr.Channels)
                {
                    writer.WriteStartArray(kv.Key);
                    foreach (var user in kv.Value) writer.WriteStringValue(user);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return Results.Text(Encoding.UTF8.GetString(stream.ToArray()), "application/json");
        }).WithTags("WebSocket").RequireAuthorization();
    }

    // Builds the outgoing WebSocket payload { type, message } using safe JSON writing.
    // The msgType is user-controlled; without escaping, quotes in it break the JSON
    // structure (e.g. type=`"evil","injected":"x` would inject a sibling key).
    private static string BuildWsMessageJson(JsonElement body)
    {
        var msgType = body.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", msgType);
            if (body.TryGetProperty("message", out var m))
            {
                writer.WritePropertyName("message");
                m.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject("message");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildSendResult(bool sent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "success");
            writer.WriteBoolean("message_sent", sent);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
