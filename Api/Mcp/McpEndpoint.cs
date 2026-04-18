using System.Text.Json;

namespace Dmart.Api.Mcp;

// HTTP routes for Model Context Protocol over Streamable HTTP.
//   POST   /mcp   — client → server JSON-RPC request, one message per body.
//                   Notifications (no `id`) get 202 Accepted with empty body;
//                   requests get 200 with the response envelope.
//   GET    /mcp   — server → client SSE stream (Phase 4 — skeleton only).
//   DELETE /mcp   — optional session close.
//
// All three require authentication (`RequireAuthorization`); the caller's JWT
// flows through to tool handlers via HttpContext.User so dmart's existing
// permission resolver enforces per-user access — no admin-token escape hatch.
public static class McpEndpoint
{
    public const string ServerProtocolVersion = "2025-03-26";
    public const string SessionHeader = "Mcp-Session-Id";

    public static IEndpointRouteBuilder MapMcp(this IEndpointRouteBuilder g)
    {
        var store = new McpSessionStore();

        g.MapPost("/mcp", async (HttpContext http, CancellationToken ct) =>
        {
            var response = await HandlePostAsync(http, store, ct);
            if (response is null)
            {
                http.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }
            http.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                http.Response.Body, response, McpJsonContext.Default.McpResponse, ct);
        }).RequireAuthorization();

        // Streamable HTTP SSE stream. Phase 4 holds the connection open with
        // periodic `:keep-alive` comments; future phases will emit real
        // server-to-client notifications (e.g. `notifications/resources/updated`
        // bridged from dmart's existing WebSocket event bus). Clients
        // disconnect by aborting the request, which flips `ct` and unblocks
        // the loop.
        g.MapGet("/mcp", async (HttpContext http, CancellationToken ct) =>
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers["Cache-Control"] = "no-cache";
            http.Response.Headers["Connection"] = "keep-alive";
            http.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx buffering
            var keepAlive = ": keep-alive\n\n"u8.ToArray();
            try
            {
                // Flush headers immediately so the client knows the stream is live.
                await http.Response.Body.WriteAsync(keepAlive, ct);
                await http.Response.Body.FlushAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    await http.Response.Body.WriteAsync(keepAlive, ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnect — clean exit */ }
        }).RequireAuthorization();

        g.MapDelete("/mcp", (HttpContext http) =>
        {
            var id = http.Request.Headers[SessionHeader].ToString();
            if (!string.IsNullOrEmpty(id)) store.Remove(id);
            return Results.NoContent();
        }).RequireAuthorization();

        return g;
    }

    // ---- core dispatch ----
    // Returns null to signal "notification — reply with 202 Accepted, no body".
    private static async Task<McpResponse?> HandlePostAsync(
        HttpContext http, McpSessionStore store, CancellationToken ct)
    {
        McpRequest? req;
        try
        {
            req = await JsonSerializer.DeserializeAsync(
                http.Request.Body, McpJsonContext.Default.McpRequest, ct);
        }
        catch (JsonException ex)
        {
            return ErrorResponse(id: null, code: -32700, message: $"parse error: {ex.Message}");
        }
        if (req is null || string.IsNullOrEmpty(req.Method))
            return ErrorResponse(id: null, code: -32600, message: "invalid request");

        // Notifications (no id) never produce a response body.
        var isNotification = !req.Id.HasValue || req.Id.Value.ValueKind == JsonValueKind.Null;

        try
        {
            switch (req.Method)
            {
                case "initialize":
                    return HandleInitialize(req, http, store);

                case "initialized":
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;  // 202 accepted

                case "ping":
                    return Ok(req.Id, EmptyObject());

                case "tools/list":
                    return Ok(req.Id, Serialize(
                        new ToolsListResult(McpRegistry.Tools),
                        McpJsonContext.Default.ToolsListResult));

                case "tools/call":
                    return await HandleToolCall(req, http, ct);

                case "resources/list":
                    // Top-level sentinel only; clients navigate by parsing
                    // entries the tools return and constructing dmart:// URIs.
                    return Ok(req.Id, Serialize(
                        new ResourcesListResult(new List<McpResource>
                        {
                            new()
                            {
                                Uri = "dmart://spaces",
                                Name = "All accessible spaces",
                                MimeType = "application/json",
                            },
                        }),
                        McpJsonContext.Default.ResourcesListResult));

                case "resources/read":
                    return await HandleResourcesRead(req, http, ct);

                default:
                    if (isNotification) return null;
                    return ErrorResponse(req.Id, -32601, $"method not found: {req.Method}");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return ErrorResponse(req.Id, -32002, ex.Message);
        }
        catch (Exception ex)
        {
            return ErrorResponse(req.Id, -32603, $"internal error: {ex.Message}");
        }
    }

    private static McpResponse HandleInitialize(
        McpRequest req, HttpContext http, McpSessionStore store)
    {
        InitializeParams? p = null;
        if (req.Params.HasValue)
        {
            try
            {
                p = JsonSerializer.Deserialize(
                    req.Params.Value, McpJsonContext.Default.InitializeParams);
            }
            catch (JsonException ex)
            {
                return ErrorResponse(req.Id, -32602, $"invalid initialize params: {ex.Message}");
            }
        }
        var clientInfo = p?.ClientInfo ?? new ClientInfo("unknown", "0");
        var clientVersion = p?.ProtocolVersion ?? ServerProtocolVersion;

        var session = store.Create(clientInfo.Name, clientInfo.Version, clientVersion);

        // Mcp-Session-Id header is how the client echoes session identity on
        // subsequent requests — emit it on the initialize response only.
        http.Response.Headers[SessionHeader] = session.Id;

        var result = new InitializeResult
        {
            ProtocolVersion = ServerProtocolVersion,
            ServerInfo = new ServerInfo("dmart", ServerVersion()),
            Capabilities = new ServerCapabilities(),
        };
        return Ok(req.Id, Serialize(result, McpJsonContext.Default.InitializeResult));
    }

    // Resources URIs:
    //   dmart://spaces                           → list of accessible spaces
    //   dmart://<space>/<subpath>/<shortname>    → one entry
    private static async Task<McpResponse> HandleResourcesRead(
        McpRequest req, HttpContext http, CancellationToken ct)
    {
        ResourcesReadParams? p = null;
        if (req.Params.HasValue)
        {
            try
            {
                p = JsonSerializer.Deserialize(
                    req.Params.Value, McpJsonContext.Default.ResourcesReadParams);
            }
            catch (JsonException ex)
            {
                return ErrorResponse(req.Id, -32602, $"invalid resources/read params: {ex.Message}");
            }
        }
        if (p is null || string.IsNullOrEmpty(p.Uri))
            return ErrorResponse(req.Id, -32602, "resources/read requires `uri`");

        try
        {
            var payloadText = await McpResourceResolver.ReadAsync(p.Uri, http, ct);
            var result = new ResourcesReadResult(new List<ResourceContents>
            {
                new()
                {
                    Uri = p.Uri,
                    MimeType = "application/json",
                    Text = payloadText,
                },
            });
            return Ok(req.Id, Serialize(result, McpJsonContext.Default.ResourcesReadResult));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ErrorResponse(req.Id, -32002, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(req.Id, -32602, ex.Message);
        }
    }

    private static async Task<McpResponse> HandleToolCall(
        McpRequest req, HttpContext http, CancellationToken ct)
    {
        ToolsCallParams? p = null;
        if (req.Params.HasValue)
        {
            try
            {
                p = JsonSerializer.Deserialize(
                    req.Params.Value, McpJsonContext.Default.ToolsCallParams);
            }
            catch (JsonException ex)
            {
                return ErrorResponse(req.Id, -32602, $"invalid tools/call params: {ex.Message}");
            }
        }
        if (p is null || string.IsNullOrEmpty(p.Name))
            return ErrorResponse(req.Id, -32602, "tools/call requires `name`");
        if (!McpRegistry.Handlers.TryGetValue(p.Name, out var handler))
            return ErrorResponse(req.Id, -32601, $"unknown tool: {p.Name}");

        try
        {
            var toolResult = await handler(p.Arguments, http, ct);
            // Single text-content item containing the JSON-serialized result.
            // Clients render this predictably and the LLM reads it as JSON.
            var content = new List<ToolContent>
            {
                new("text", JsonSerializer.Serialize(toolResult, McpJsonContext.Default.JsonElement)),
            };
            return Ok(req.Id, Serialize(
                new ToolsCallResult(content), McpJsonContext.Default.ToolsCallResult));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ErrorResponse(req.Id, -32002, ex.Message);
        }
        catch (Exception ex)
        {
            // MCP convention: for tool errors (as opposed to protocol errors),
            // return success with isError=true so the model can see + react.
            var content = new List<ToolContent> { new("text", $"error: {ex.Message}") };
            return Ok(req.Id, Serialize(
                new ToolsCallResult(content, IsError: true),
                McpJsonContext.Default.ToolsCallResult));
        }
    }

    // ---- helpers ----

    private static McpResponse Ok(JsonElement? id, JsonElement? result)
        => new() { Id = id, Result = result };

    private static McpResponse ErrorResponse(JsonElement? id, int code, string message)
        => new() { Id = id, Error = new McpError(code, message) };

    private static JsonElement Serialize<T>(
        T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, info);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private static JsonElement EmptyObject()
        => JsonDocument.Parse("{}").RootElement.Clone();

    private static string ServerVersion()
    {
        var asm = typeof(McpEndpoint).Assembly;
        var attrs = asm.GetCustomAttributes(
            typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        if (attrs.Length > 0 &&
            attrs[0] is System.Reflection.AssemblyInformationalVersionAttribute a)
            return a.InformationalVersion;
        return "dev";
    }
}
