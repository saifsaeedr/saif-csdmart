# MCP — Model Context Protocol server, in-process

In-process Model Context Protocol server bolted onto the existing Kestrel host.
Exposes dmart's data model to any MCP-capable AI client (Claude Desktop,
Cursor, Zed, Continue, VS Code Copilot, ChatGPT, Gemini) as tools + resources
the model can call **on behalf of the authenticated user**, with dmart's
existing permission resolver enforcing every read.

## Why hand-rolled

The official `ModelContextProtocol` NuGet SDK uses runtime reflection for tool
discovery (`[McpServerTool]` attribute scanning via `WithToolsFromAssembly()`),
which trips IL2026/IL3050 warnings under `PublishAot=true`. dmart is strictly
AOT, so we hand-roll a minimal server using the public JSON-RPC 2.0 +
Streamable HTTP wire protocol instead. It's ~500 LOC total, no new
dependencies.

MCP spec version pinned: **2025-03-26**.

## Endpoints

All three require authentication (`Authorization: Bearer <jwt>`) — the JWT
flows through to tool handlers, so a call sees exactly what the user would see
via CXB or `curl`.

| Method   | Path   | Purpose                                              |
|----------|--------|------------------------------------------------------|
| `POST`   | `/mcp` | Client → server JSON-RPC request. One message per body. Notifications (no `id`) return 202, requests return 200 with the response envelope. |
| `GET`    | `/mcp` | Server → client SSE stream. Held open with 15 s keep-alive comments. v0.1 emits no real events yet. |
| `DELETE` | `/mcp` | Optional session close (client sends the `Mcp-Session-Id` header). |

## Tools (v0.1)

All read-only. Create/update/delete arrive in v0.2 with MCP elicitation
confirmation for destructive operations.

| Name            | Input                                                    | Delegates to                               |
|-----------------|----------------------------------------------------------|--------------------------------------------|
| `dmart.me`      | —                                                        | `UserService` + `AccessRepository`         |
| `dmart.spaces`  | —                                                        | `QueryService` (type=spaces)               |
| `dmart.query`   | `space_name`, `subpath?`, `type?`, `resource_types?`, `filter_shortnames?`, `search?`, `limit?` (hard cap 50) | `QueryService.ExecuteAsync` |
| `dmart.read`    | `space_name`, `subpath?`, `shortname`, `resource_type?`  | `QueryService` (single-shortname filter)   |
| `dmart.schema`  | `space_name`, `shortname`                                | `QueryService` against `/schema` subpath   |

## Resources

```
dmart://spaces                                → list of accessible spaces
dmart://<space>                               → root-level entries of the space
dmart://<space>/<subpath>/                    → collection listing (trailing slash)
dmart://<space>/<subpath>/<shortname>         → one entry
```

`resources/list` currently returns only the `dmart://spaces` sentinel; the
rest are discoverable by navigating into query results.

## Configuring an MCP client

### Claude Desktop / Cursor / Zed (remote HTTP transport)

```json
{
  "mcpServers": {
    "dmart": {
      "url": "http://127.0.0.1:5099/mcp",
      "headers": {
        "Authorization": "Bearer eyJhbGci..."
      }
    }
  }
}
```

Grab the JWT from a successful `POST /user/login` response (or any existing
`auth_token` cookie). Default JWT lifetime is 15 minutes — long Claude
sessions may hit an expiry; v0.1 surfaces a clean `-32002 Unauthenticated`
MCP error when that happens. OAuth 2.1 transport is planned for v0.5.

### Quick curl smoke

```bash
TOKEN=$(curl -s -H 'Content-Type: application/json' \
  -d '{"shortname":"dmart","password":"Test1234"}' \
  http://127.0.0.1:5099/user/login \
  | jq -r '.records[0].attributes.access_token')

curl -s -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}' \
  http://127.0.0.1:5099/mcp | jq .

curl -s -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  http://127.0.0.1:5099/mcp | jq .

curl -s -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"dmart.me","arguments":{}}}' \
  http://127.0.0.1:5099/mcp | jq .
```

## Adding a new tool

1. Write the handler in `McpTools.cs` — signature
   `Task<JsonElement>(JsonElement? args, HttpContext http, CancellationToken ct)`.
   Read args out of the `JsonElement?`, resolve services via
   `http.RequestServices.GetRequiredService<T>()`, use
   `http.User.Identity?.Name` as the actor.
2. Register it in `McpRegistry.Handlers` (one line) and append a
   `McpTool` descriptor to `McpRegistry.Tools` (name + description +
   `inputSchema` as a JSON literal).
3. Add an integration test in `McpEndpointTests.cs` calling
   `tools/call` with the new name.

No attributes, no reflection — explicit registration matches dmart's overall
source-gen discipline.

## Safety defaults

- **Hard 50-result cap** on `dmart.query` regardless of what the model asks
  for. Stops runaway queries from eating the model's context window.
- **Attachment bytes never inline** — v0.1 tools return metadata only. A
  dedicated `dmart.download` tool with size guards will land in v0.3.
- **401 → clean MCP error** (`-32002 Unauthenticated`), no stack traces.
- **Tool errors surface via `ToolsCallResult.IsError=true`** rather than as
  JSON-RPC errors, so the model can see the message and react.

## Roadmap

- **v0.1** (shipped): read-only tools + resources, SSE skeleton.
- **v0.2**: `dmart.create`, `dmart.update`, `dmart.delete` with MCP
  elicitation confirmation on destructive ops.
- **v0.3**: `dmart.download` (attachment bytes, size-gated), `dmart.history`.
- **v0.4**: `dmart.semantic_search` — blocked on pgvector integration.
- **v0.5**: OAuth 2.1 HTTP transport (removes the `Authorization: Bearer`
  env-var requirement), real SSE notifications bridging dmart's existing
  WebSocket event bus into MCP's `notifications/resources/updated`.
