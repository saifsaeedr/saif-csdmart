using System.Text.Json;

namespace Dmart.Api.Mcp;

// Tool handler delegate — receives the JSON-RPC `arguments` element, the
// HTTP context (for caller identity + DI scope), and a cancellation token.
// Returns a JsonElement that becomes the text content of the ToolsCallResult
// (McpEndpoint serializes it to JSON-formatted text, which clients render
// well and the LLM reads back as JSON).
public delegate Task<JsonElement> McpToolHandler(
    JsonElement? arguments, HttpContext http, CancellationToken ct);

// Static catalog of v0.1 tools. Adding a tool = append to the Tools list +
// entry in the Handlers map + a method in McpTools. No reflection, no
// attribute scanning — explicit registration matches dmart's source-gen
// discipline and keeps AOT publish free of IL2026 warnings.
public static class McpRegistry
{
    public static IReadOnlyList<McpTool> Tools { get; } = BuildTools();

    public static IReadOnlyDictionary<string, McpToolHandler> Handlers { get; } =
        new Dictionary<string, McpToolHandler>(StringComparer.Ordinal)
        {
            ["dmart.me"]      = McpTools.MeAsync,
            ["dmart.spaces"]  = McpTools.SpacesAsync,
            ["dmart.query"]   = McpTools.QueryAsync,
            ["dmart.read"]    = McpTools.ReadAsync,
            ["dmart.schema"]  = McpTools.SchemaAsync,
        };

    private static List<McpTool> BuildTools() =>
    [
        new McpTool
        {
            Name = "dmart.me",
            Description = "Returns the caller's identity — shortname, email, " +
                          "roles, groups, language, and the list of accessible " +
                          "permission keys (space:subpath:resource_type). Call " +
                          "this first to know what you can see and act on.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {},
                  "required": [],
                  "additionalProperties": false
                }
                """),
        },
        new McpTool
        {
            Name = "dmart.spaces",
            Description = "Lists the spaces the caller has any access to. Use " +
                          "this to discover top-level containers before issuing " +
                          "a more specific query.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {},
                  "required": [],
                  "additionalProperties": false
                }
                """),
        },
        new McpTool
        {
            Name = "dmart.query",
            Description = "Runs a query against dmart and returns up to 50 " +
                          "matching records. Permissions are enforced — the " +
                          "caller only sees what they're allowed to see. " +
                          "Results include resource_type, shortname, subpath, " +
                          "and attributes.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "space_name":        { "type": "string", "description": "Space to query (e.g. 'management')." },
                    "subpath":           { "type": "string", "description": "Subpath within the space. Defaults to '/'." },
                    "type":              { "type": "string", "enum": ["search","spaces","history","attachments","tags","counters","aggregation","events"], "description": "Query type. Defaults to 'search'." },
                    "resource_types":    { "type": "array", "items": { "type": "string" }, "description": "Filter by resource types (content, folder, user, role, permission, schema, ticket, ...)." },
                    "filter_shortnames": { "type": "array", "items": { "type": "string" }, "description": "Only return records matching these shortnames." },
                    "search":            { "type": "string", "description": "Full-text search string." },
                    "limit":             { "type": "integer", "minimum": 1, "maximum": 50, "description": "Max records to return. Hard-capped at 50." }
                  },
                  "required": ["space_name"],
                  "additionalProperties": false
                }
                """),
        },
        new McpTool
        {
            Name = "dmart.read",
            Description = "Reads a single entry by (space_name, subpath, " +
                          "shortname, resource_type). Returns the entry's " +
                          "full attributes + payload body. Respects caller " +
                          "permissions — returns empty records if not visible.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "space_name":    { "type": "string" },
                    "subpath":       { "type": "string", "description": "Subpath. Defaults to '/'." },
                    "shortname":     { "type": "string" },
                    "resource_type": { "type": "string", "description": "Resource type. Defaults to 'content'." }
                  },
                  "required": ["space_name","shortname"],
                  "additionalProperties": false
                }
                """),
        },
        new McpTool
        {
            Name = "dmart.schema",
            Description = "Fetches a JSON Schema definition from the space's " +
                          "/schema subpath. Use BEFORE calling any create/" +
                          "update tool (future) to ensure the attributes + " +
                          "payload you supply are schema-valid.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "space_name": { "type": "string" },
                    "shortname":  { "type": "string", "description": "Schema shortname (the entry under /schema)." }
                  },
                  "required": ["space_name","shortname"],
                  "additionalProperties": false
                }
                """),
        },
    ];

    // Parse a JSON Schema literal into a JsonElement for the tool descriptor.
    // Safe — all inputs here are compile-time constants.
    internal static JsonElement ParseSchema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
