using System.Collections.Concurrent;

namespace Dmart.Api.Mcp;

// Per-session state. MCP identifies sessions by the `Mcp-Session-Id` header,
// which the server generates during `initialize` and the client echoes on
// every subsequent request. We track the minimum necessary: the client's
// declared info + protocol version, so tool handlers can behave differently
// for older clients if ever needed. Phase 1 doesn't read anything from the
// session beyond its existence — it's groundwork for SSE notifications + the
// delete-session endpoint.
public sealed record McpSessionState(
    string Id,
    string ClientName,
    string ClientVersion,
    string ProtocolVersion,
    DateTime CreatedAt);

// In-memory session registry. No persistence — restarts drop all sessions,
// which is fine: MCP clients reconnect by re-calling `initialize`.
public sealed class McpSessionStore
{
    private readonly ConcurrentDictionary<string, McpSessionState> _sessions = new();

    public McpSessionState Create(string clientName, string clientVersion, string protocolVersion)
    {
        var id = Guid.NewGuid().ToString("N");
        var state = new McpSessionState(id, clientName, clientVersion, protocolVersion, DateTime.UtcNow);
        _sessions[id] = state;
        return state;
    }

    public McpSessionState? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public bool Remove(string id) => _sessions.TryRemove(id, out _);
}
