using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dmart.Api.Mcp;

// Source-generated JSON for the MCP wire format. Separate from DmartJsonContext
// because MCP uses camelCase property names (protocolVersion, inputSchema,
// serverInfo, isError, mimeType, ...) while dmart's main API uses
// snake_case. Mixing the two conventions in one context would require
// per-property JsonPropertyName overrides — cleaner to keep them apart.
//
// All types registered here MUST be AOT-safe: records with scalar + JsonElement
// fields, no polymorphism, no dictionaries-of-object except Dictionary<string,
// JsonElement> where we already own the shape.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpRequest))]
[JsonSerializable(typeof(McpResponse))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ClientInfo))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(ServerCapabilities))]
[JsonSerializable(typeof(ToolsCapability))]
[JsonSerializable(typeof(ResourcesCapability))]
[JsonSerializable(typeof(ToolsListResult))]
[JsonSerializable(typeof(McpTool))]
[JsonSerializable(typeof(ToolsCallParams))]
[JsonSerializable(typeof(ToolsCallResult))]
[JsonSerializable(typeof(ToolContent))]
[JsonSerializable(typeof(ResourcesListResult))]
[JsonSerializable(typeof(McpResource))]
[JsonSerializable(typeof(ResourcesReadParams))]
[JsonSerializable(typeof(ResourcesReadResult))]
[JsonSerializable(typeof(ResourceContents))]
[JsonSerializable(typeof(JsonElement))]
public partial class McpJsonContext : JsonSerializerContext;
