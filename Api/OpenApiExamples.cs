using System.Text.Json.Nodes;
using Dmart.Models.Api;
// Docs-only request DTOs (SemanticSearchBody, ProfileUpdateBody, …) live in
// Dmart.Models.Api as well; the explicit using above already covers them.

namespace Dmart.Api;

// Sample payloads shown in Swagger UI for each request body type. Inspired
// by the FastAPI parity contract: every endpoint that takes a typed body
// should pre-fill the "Try it out" form with a payload that hits the
// happy path, so a developer can land on /docs/, click an endpoint, and
// fire a working request with no edits beyond auth.
//
// Adding a new request body type? Drop a (typeof(T), JsonNode) entry here
// and the OpenAPI schema transformer will surface it. The docs-only
// request DTOs (SemanticSearchBody, ProfileUpdateBody, ...) live in DocsDtos.cs.
internal static class OpenApiExamples
{
    // Built lazily and cached: JsonNode is mutable and the OpenAPI
    // generator may reuse the schema across multiple operations, but each
    // schema gets its own clone via `JsonNode.DeepClone()` before being
    // attached — the source `_examples` dict must therefore stay constant.
    private static readonly Dictionary<Type, JsonNode> _examples = new()
    {
        // ─── User auth ───────────────────────────────────────────────
        [typeof(UserLoginRequest)] = JsonNode.Parse("""
            {
              "shortname": "alibaba",
              "password": "Password1234"
            }
            """)!,

        [typeof(SendOTPRequest)] = JsonNode.Parse("""
            {
              "msisdn": "9647811223344"
            }
            """)!,

        [typeof(ConfirmOTPRequest)] = JsonNode.Parse("""
            {
              "code": "123456",
              "msisdn": "9647811223344"
            }
            """)!,

        [typeof(PasswordResetRequest)] = JsonNode.Parse("""
            {
              "shortname": "alibaba"
            }
            """)!,

        // Record envelope — reused as the example for every Record-bodied
        // endpoint (/managed/request, /public/submit, etc.). Note that
        // /user/create has its own dedicated UserCreateBody example below
        // (shortname is server-allocated there and not part of the wire
        // shape). If a different endpoint warrants its own shape, lift
        // it onto a docs-only DTO (see DocsDtos.cs).
        [typeof(Record)] = JsonNode.Parse("""
            {
              "resource_type": "content",
              "shortname": "my_entry",
              "subpath": "/items",
              "attributes": {
                "is_active": true,
                "payload": {
                  "content_type": "json",
                  "body": { "title": "Hello" }
                }
              }
            }
            """)!,

        // POST /user/create — server allocates shortname + uuid. Callers
        // send only `attributes`. Any extra top-level fields are silently
        // ignored as unknown JSON properties.
        [typeof(UserCreateBody)] = JsonNode.Parse("""
            {
              "attributes": {
                "email": "newuser@example.com",
                "password": "Password1234",
                "displayname": { "en": "New User" },
                "roles": []
              }
            }
            """)!,

        [typeof(ValidatePasswordBody)] = JsonNode.Parse("""
            {
              "password": "Password1234"
            }
            """)!,

        [typeof(ProfileUpdateBody)] = JsonNode.Parse("""
            {
              "displayname": { "en": "Updated Name" },
              "language": "english"
            }
            """)!,

        [typeof(OAuthMobileLoginBody)] = JsonNode.Parse("""
            {
              "token": "eyJhbGciOi... id/access token from the provider SDK"
            }
            """)!,

        // ─── OAuth dynamic client registration ──────────────────────
        [typeof(RegisterRequest)] = JsonNode.Parse("""
            {
              "redirect_uris": ["http://127.0.0.1:6274/callback"],
              "client_name": "mcp-client"
            }
            """)!,

        // ─── Managed: query & CRUD ──────────────────────────────────
        // /managed/query — the canonical fetch. Returns every user entry
        // under management/users with full attributes + a row count.
        [typeof(Query)] = JsonNode.Parse("""
            {
              "type": "search",
              "space_name": "management",
              "subpath": "/users",
              "retrieve_json_payload": true,
              "retrieve_attachments": false,
              "retrieve_total": true,
              "limit": 10,
              "offset": 0
            }
            """)!,

        // /managed/query → join — example of the inner JoinQuery element.
        // Surfaces the optional `type` field (left/right/inner/outer) so
        // a developer reading the schema knows it's there; otherwise the
        // source-gen-derived schema shows the enum without an example.
        [typeof(JoinQuery)] = JsonNode.Parse("""
            {
              "join_on": "payload.body.customer:shortname",
              "alias": "customer",
              "type": "left",
              "query": {
                "type": "subpath",
                "space_name": "management",
                "subpath": "/customers",
                "limit": 100,
                "retrieve_json_payload": true
              }
            }
            """)!,

        // /managed/request — the canonical CRUD envelope. This sample
        // creates a single content entry; swap request_type to update /
        // replace / delete / move and adjust the record block accordingly.
        [typeof(Request)] = JsonNode.Parse("""
            {
              "space_name": "management",
              "request_type": "create",
              "records": [
                {
                  "resource_type": "content",
                  "shortname": "sample_note",
                  "subpath": "/notes",
                  "attributes": {
                    "is_active": true,
                    "displayname": { "en": "Sample note" },
                    "payload": {
                      "content_type": "json",
                      "body": { "title": "hello", "tags": ["demo"] }
                    }
                  }
                }
              ]
            }
            """)!,

        // ─── Managed: niche endpoints ───────────────────────────────
        [typeof(SemanticSearchBody)] = JsonNode.Parse("""
            {
              "query": "blue widget pricing",
              "space_name": "management",
              "subpath": "/products",
              "limit": 5
            }
            """)!,

        [typeof(ReindexEmbeddingsBody)] = JsonNode.Parse("""
            {
              "space_name": "management",
              "only_missing": true,
              "max_per_space": 5000
            }
            """)!,

        [typeof(ExecuteTaskBody)] = JsonNode.Parse("""
            {
              "shortname": "my_saved_query",
              "subpath": "/queries"
            }
            """)!,

        [typeof(ProgressTicketBody)] = JsonNode.Parse("""
            {
              "resolution_reason": "fixed",
              "comment": "verified on staging"
            }
            """)!,

        // ─── WebSocket bridge ────────────────────────────────────────
        [typeof(WsSendMessageBody)] = JsonNode.Parse("""
            {
              "type": "notification",
              "message": { "title": "hello", "body": "you have mail" }
            }
            """)!,

        [typeof(WsBroadcastBody)] = JsonNode.Parse("""
            {
              "type": "update",
              "message": { "resource": "ticket/123" },
              "channels": ["tickets", "alerts"]
            }
            """)!,

        // ─── MCP ─────────────────────────────────────────────────────
        [typeof(McpRequestBody)] = JsonNode.Parse("""
            {
              "jsonrpc": "2.0",
              "id": 1,
              "method": "initialize",
              "params": {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": { "name": "demo-client", "version": "0.1" }
              }
            }
            """)!,
    };

    public static bool TryGet(Type type, out JsonNode example)
    {
        if (_examples.TryGetValue(type, out var node))
        {
            example = node.DeepClone();
            return true;
        }
        example = null!;
        return false;
    }
}
