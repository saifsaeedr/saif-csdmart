namespace Dmart.Models.Api;

// Documentation-only request shapes. Every endpoint here parses its body
// manually via HttpRequest / JsonDocument, so the minimal-API binder never
// sees these types — they exist purely to give the OpenAPI generator a
// stable schema to emit so Swagger UI's "Try it out" form can pre-fill a
// payload. The matching sample payloads live in Dmart.Api.OpenApiExamples.
//
// Adding a new endpoint that takes a body? Either reuse one of these or
// drop a new record below + an example + a `.Accepts<T>("application/json")`
// on the endpoint registration. Each new record MUST also be listed in
// DmartJsonContext — the source-gen resolver is the default for HTTP JSON,
// and the OpenAPI generator queries it for type metadata.
//
// DRIFT RISK — read before editing: these DTOs are *parallel* declarations
// of the body shapes their endpoints accept. They do NOT drive parsing
// (those endpoints read HttpRequest directly), so the C# compiler can't
// catch a mismatch. If you add or remove a field a handler reads, you MUST
// reflect it here or Swagger silently misleads users. The OpenApi smoke
// tests (dmart.Tests/Integration/OpenApiDocumentTests.cs) catch some
// shapes of drift (every $ref resolves, every multipart path exists),
// but field-level handler↔DTO drift is not automatically detected —
// spot-check against the handler when editing either side.

// /user/validate_password — verify the current user's password without
// mutating anything. Used by UIs gating sensitive flows.
public sealed record ValidatePasswordBody(string Password);

// /user/profile — partial update. Every field optional; only the fields
// you send get changed. `payload.body` is free-form per the user schema.
public sealed record ProfileUpdateBody(
    string? Password = null,
    string? OldPassword = null,
    string? Email = null,
    string? Msisdn = null,
    Dictionary<string, string>? Displayname = null,
    Dictionary<string, string>? Description = null,
    string? Language = null,
    Dictionary<string, object>? Payload = null,
    string? FirebaseToken = null);

// /managed/semantic-search — vector search via the configured embedding
// provider. `query` is required; the rest narrow the result set.
public sealed record SemanticSearchBody(
    string Query,
    string? SpaceName = null,
    string? Subpath = null,
    List<string>? ResourceTypes = null,
    int? Limit = null);

// /managed/reindex-embeddings — admin tool. All fields optional;
// `only_missing` defaults to true server-side.
public sealed record ReindexEmbeddingsBody(
    string? SpaceName = null,
    bool? OnlyMissing = null,
    int? MaxPerSpace = null);

// /managed/execute and /public/excute — back-compat body shape
// (`{shortname, subpath?, query_overrides?}`). Both endpoints also
// accept a raw Record envelope — see Record schema for that form.
public sealed record ExecuteTaskBody(
    string Shortname,
    string? Subpath = null,
    Dictionary<string, object>? QueryOverrides = null);

// PUT /managed/progress-ticket/{...} — optional body; the handler reads
// just these three keys, anything else is ignored.
public sealed record ProgressTicketBody(
    string? Resolution = null,
    string? ResolutionReason = null,
    string? Comment = null);

// /user/{provider}/mobile-login — provider-specific id/access token from
// the mobile SDK. The handler validates against the provider and mints a
// dmart session cookie.
public sealed record OAuthMobileLoginBody(string Token);

// /send-message/{user_shortname} — plugin-to-server push. `type` routes
// to the client-side handler; `message` is the payload object passed
// through unchanged.
public sealed record WsSendMessageBody(
    string Type,
    object? Message = null);

// /broadcast-to-channels — fan-out to every subscribed client of each
// listed channel.
public sealed record WsBroadcastBody(
    string Type,
    object? Message = null,
    List<string>? Channels = null);

// /mcp — Model Context Protocol JSON-RPC envelope. `id` is null for
// notifications (server replies 202 Accepted with no body).
public sealed record McpRequestBody(
    string Jsonrpc,
    string Method,
    object? Params = null,
    object? Id = null);
