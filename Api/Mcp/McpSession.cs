using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Dmart.Api.Mcp;

// Per-session state. MCP identifies sessions by the `Mcp-Session-Id` header,
// which the server generates during `initialize` and the client echoes on
// every subsequent request.
//
// Beyond the client-info bookkeeping, the session owns two runtime hooks that
// the push path needs:
//   * Outbox — Channel<string> holding server→client SSE frames. Writers
//     (event bus bridge + elicitation sender) enqueue; GET /mcp reader
//     drains and writes to the response stream.
//   * PendingElicitations — TaskCompletionSource map keyed by the server-
//     originated request id. When the client POSTs back a response, the
//     POST handler resolves the TCS so the awaiter inside the tool handler
//     wakes up.
public sealed class McpSessionState
{
    public required string Id { get; init; }
    public required string ClientName { get; init; }
    public required string ClientVersion { get; init; }
    public required string ProtocolVersion { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string? UserShortname { get; set; }
    public bool ElicitationSupported { get; set; }

    // Bounded so a disconnected client can't let us grow unboundedly; a slow
    // consumer eventually drops the oldest frames rather than blocking the
    // event bus.
    internal Channel<string> Outbox { get; } =
        Channel.CreateBounded<string>(new BoundedChannelOptions(capacity: 256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    internal ConcurrentDictionary<string, TaskCompletionSource<System.Text.Json.JsonElement>>
        PendingElicitations { get; } = new();

    // Try to enqueue a server→client message. Returns false if the outbox is
    // closed (no active GET /mcp reader).
    public bool TryEnqueue(string sseData) => Outbox.Writer.TryWrite(sseData);
}

// In-memory session registry. No persistence — restarts drop all sessions,
// which is fine: MCP clients reconnect by re-calling `initialize`.
public sealed class McpSessionStore
{
    private readonly ConcurrentDictionary<string, McpSessionState> _sessions = new();

    public McpSessionState Create(string clientName, string clientVersion, string protocolVersion)
    {
        var id = Guid.NewGuid().ToString("N");
        var state = new McpSessionState
        {
            Id = id,
            ClientName = clientName,
            ClientVersion = clientVersion,
            ProtocolVersion = protocolVersion,
            CreatedAt = DateTime.UtcNow,
        };
        _sessions[id] = state;
        return state;
    }

    public McpSessionState? Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

    public bool Remove(string id) => _sessions.TryRemove(id, out _);

    // Snapshot of all sessions owned by a given user. Used by the event bus
    // bridge to fan out a single dmart event to every MCP session that user
    // currently has open (Claude Desktop + Cursor + Zed, etc.).
    public IEnumerable<McpSessionState> ByUser(string userShortname)
    {
        foreach (var s in _sessions.Values)
            if (string.Equals(s.UserShortname, userShortname, StringComparison.Ordinal))
                yield return s;
    }

    public IEnumerable<McpSessionState> All() => _sessions.Values;
}
