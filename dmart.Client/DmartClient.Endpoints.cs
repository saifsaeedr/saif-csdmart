using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
#if NET8_0_OR_GREATER
using Dmart.Client.Json;
#endif

namespace Dmart.Client;

// Server-endpoint coverage: the wrappers that fill the gap between dmart's
// public HTTP API and the typed client SDK. Auth/identity wrappers stay in
// DmartClient.cs / DmartClient.Extra.cs (Client-only by design). Everything
// here mirrors a single dmart server route 1:1.
//
// Scope:
//   - Locks, WebSocket admin, account deletion, "me", admin (reindex,
//     alteration, security reload, semantic search), bulk (import/export,
//     CSV ingest), public attach, short links, public/query GET, public/
//     execute, and the three social mobile-login flows.
//   - SKIPPED (transport-specific or non-SDK consumer):
//       MCP session endpoints (require streaming/SSE state machinery),
//       QR generate/validate (niche; consumers can construct URLs directly),
//       OAuth 2.0 server endpoints (consumed by OAuth clients, not SDKs),
//       OIDC discovery endpoints,
//       OAuth web callback redirects (browser-only).
public sealed partial class DmartClient
{
    // -------------------------------------------------------------------
    // Locks
    // -------------------------------------------------------------------

    // PUT /managed/lock/{resource_type}/{space}/{subpath}/{shortname}.
    // Returns the lock record on success; throws DmartException on a
    // permission denial or active conflicting lock.
    public async Task<Response> LockEntryAsync(
        ResourceType resourceType, string spaceName, string subpath, string shortname,
        CancellationToken ct = default)
    {
        var rt = ResourceTypeWire(resourceType);
        subpath = NormalizeSubpath(subpath).TrimStart('/');
        var path = string.IsNullOrEmpty(subpath)
            ? $"/managed/lock/{rt}/{spaceName}/{shortname}"
            : $"/managed/lock/{rt}/{spaceName}/{subpath}/{shortname}";
        using var req = BuildRequest(HttpMethod.Put, path);
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // DELETE /managed/lock/{space}/{subpath}/{shortname}.
    public async Task<Response> UnlockEntryAsync(
        string spaceName, string subpath, string shortname,
        CancellationToken ct = default)
    {
        subpath = NormalizeSubpath(subpath).TrimStart('/');
        var path = string.IsNullOrEmpty(subpath)
            ? $"/managed/lock/{spaceName}/{shortname}"
            : $"/managed/lock/{spaceName}/{subpath}/{shortname}";
        using var req = BuildRequest(HttpMethod.Delete, path);
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // WebSocket admin (server publishes to subscribed clients)
    // -------------------------------------------------------------------

    // POST /send-message/{user_shortname}. Body is the message envelope —
    // the server passes through whatever JSON shape the caller sends.
    public async Task<JsonDocument> SendMessageAsync(
        string userShortname, Dictionary<string, object?> message,
        CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post,
            $"/send-message/{Uri.EscapeDataString(userShortname)}",
            Json(message));
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // POST /broadcast-to-channels. `message` must include a "channels" key
    // (list of channel names) plus the payload the server forwards verbatim.
    public async Task<JsonDocument> BroadcastToChannelsAsync(
        Dictionary<string, object?> message, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "/broadcast-to-channels", Json(message));
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // GET /ws-info — list connected clients + channels.
    public async Task<JsonDocument> GetWsInfoAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/ws-info");
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // User account deletion (Client-only — auth-adjacent)
    // -------------------------------------------------------------------

    // POST /user/delete — destroy the caller's own account. Clears the
    // bearer token afterwards regardless of outcome.
    public async Task<Response> DeleteAccountAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Post, "/user/delete");
            return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
        }
        finally
        {
            _authToken = null;
        }
    }

    // -------------------------------------------------------------------
    // Info — server self-introspection
    // -------------------------------------------------------------------

    // GET /info/me — minimal "who am I" probe. Reads the JWT-bound identity.
    public async Task<Response> GetInfoMeAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/info/me");
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Admin — server-side bookkeeping (require elevated permissions)
    // -------------------------------------------------------------------

    // POST /managed/reindex-embeddings — rebuild semantic-search indexes
    // for a space.
    public async Task<Response> ReindexEmbeddingsAsync(string spaceName, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["space_name"] = spaceName };
        using var req = BuildRequest(HttpMethod.Post, "/managed/reindex-embeddings", Json(body));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /managed/apply-alteration/{space}/{alteration_name} — apply a
    // pre-staged alteration to a space.
    public async Task<Response> ApplyAlterationAsync(
        string spaceName, string alterationName, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post,
            $"/managed/apply-alteration/{Uri.EscapeDataString(spaceName)}/{Uri.EscapeDataString(alterationName)}");
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // GET /managed/reload-security-data — invalidate the in-memory
    // role/permission cache after admin edits.
    public async Task<Response> ReloadSecurityDataAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/managed/reload-security-data");
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /managed/semantic-search — vector/embedding search. Body is the
    // semantic-search payload (space_name, query, limit, …). Body shape is
    // server-defined; using a generic dict keeps the SDK in step without
    // hard-coding a fragile DTO.
    public async Task<Response> SemanticSearchAsync(
        Dictionary<string, object?> body, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "/managed/semantic-search", Json(body));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Bulk import / export
    // -------------------------------------------------------------------

    // POST /managed/import — bulk-import a JSON payload (typically a
    // previously-exported envelope). Body shape varies by import mode.
    public async Task<Response> ImportAsync(
        Dictionary<string, object?> body, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "/managed/import", Json(body));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /managed/export — export per the body's filters. Returns the
    // raw export document (may be a streamed envelope or a file-style
    // payload depending on caller flags).
    public async Task<JsonDocument> ExportAsync(
        Dictionary<string, object?> body, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "/managed/export", Json(body));
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // POST /managed/resources_from_csv/{resource_type}/{space}/{subpath} —
    // bulk-create entries from a CSV file. Multipart upload, same content
    // structure as UploadWithPayloadAsync. Pass isUpdate=true to deep-merge
    // each row's columns into the existing entry's payload.body instead of
    // creating new entries.
    //
    // `isUpdate` sits AFTER `ct` deliberately — inserting it before the
    // CancellationToken would have been a binary-break for any compiled
    // caller against the prior Dmart.Client.0.9.2 surface (positional
    // callers, recompile required). End-of-list keeps the SDK contract
    // additive.
    public async Task<Response> ResourcesFromCsvAsync(
        ResourceType resourceType, string spaceName, string subpath,
        byte[] csvBytes, string csvFileName = "import.csv",
        string schemaShortname = "", CancellationToken ct = default,
        bool isUpdate = false)
    {
        var rt = ResourceTypeWire(resourceType);
        subpath = NormalizeSubpath(subpath).TrimStart('/');
        var path = string.IsNullOrEmpty(subpath)
            ? $"/managed/resources_from_csv/{rt}/{spaceName}"
            : $"/managed/resources_from_csv/{rt}/{spaceName}/{subpath}";
        if (!string.IsNullOrEmpty(schemaShortname)) path += $"/{schemaShortname}";
        // `?` if no existing query string, `&` if one. Future-proofs against
        // a future change that puts other query params on this path.
        if (isUpdate) path += (path.Contains('?') ? "&" : "?") + "is_update=true";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(csvBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "resources_file", csvFileName);

        using var req = BuildRequest(HttpMethod.Post, path, form);
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /public/attach/{space_name} — anonymous attachment upload to a
    // public folder. Multipart with the same shape as UploadWithPayloadAsync.
    public async Task<Response> PublicAttachAsync(
        string spaceName, Record record, byte[] payloadBytes, string payloadFileName,
        string payloadMime = "application/octet-stream",
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(spaceName), "space_name");

#if NET8_0_OR_GREATER
        var recordJson = JsonSerializer.Serialize(record, DmartClientJsonContext.Default.Record);
#else
        var recordJson = JsonSerializer.Serialize(record, DefaultJsonOptions);
#endif
        var recordContent = new StringContent(recordJson);
        recordContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(recordContent, "request_record", "request_record.json");

        var fileContent = new ByteArrayContent(payloadBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(payloadMime);
        form.Add(fileContent, "payload_file", payloadFileName);

        using var req = BuildRequest(HttpMethod.Post, $"/public/attach/{Uri.EscapeDataString(spaceName)}", form);
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Short links
    // -------------------------------------------------------------------

    // GET /managed/s/{token} — follow a short link. Server returns a
    // redirect; we surface the raw JSON envelope (server may also stream
    // the target).
    public async Task<JsonDocument> GetShortLinkAsync(string token, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, $"/managed/s/{Uri.EscapeDataString(token)}");
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // GET /managed/shortening/{space}/{subpath} — list / inspect short links
    // for a subpath.
    public async Task<JsonDocument> GetShorteningAsync(
        string spaceName, string subpath, CancellationToken ct = default)
    {
        subpath = NormalizeSubpath(subpath).TrimStart('/');
        var path = string.IsNullOrEmpty(subpath)
            ? $"/managed/shortening/{Uri.EscapeDataString(spaceName)}"
            : $"/managed/shortening/{Uri.EscapeDataString(spaceName)}/{subpath}";
        using var req = BuildRequest(HttpMethod.Get, path);
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Public query / execute (anonymous-eligible reads + task triggers)
    // -------------------------------------------------------------------

    // GET /public/query/{type}/{space}/{subpath} — typed query GET variant.
    // The POST variant is exposed via QueryAsync(query, scope="public").
    public async Task<Response> PublicQueryGetAsync(
        QueryType type, string spaceName, string subpath, CancellationToken ct = default)
    {
        var typeWire = QueryTypeWire(type);
        subpath = NormalizeSubpath(subpath).TrimStart('/');
        var path = string.IsNullOrEmpty(subpath)
            ? $"/public/query/{typeWire}/{Uri.EscapeDataString(spaceName)}"
            : $"/public/query/{typeWire}/{Uri.EscapeDataString(spaceName)}/{subpath}";
        using var req = BuildRequest(HttpMethod.Get, path);
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /public/excute/{task_type}/{space_name} — server-side trigger
    // for a registered task plugin. NOTE: the server's route is literally
    // spelled "/excute" (typo). The wrapper preserves that path so callers
    // hit the right route; downstream rename of the server route would
    // require flipping this string.
    public async Task<Response> PublicExecuteAsync(
        string taskType, string spaceName, Dictionary<string, object?> body,
        CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post,
            $"/public/excute/{Uri.EscapeDataString(taskType)}/{Uri.EscapeDataString(spaceName)}",
            Json(body));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Social mobile-login (Client-only — auth-adjacent)
    //
    // The /callback web routes are browser-only redirects and intentionally
    // not wrapped. Native mobile clients call these /mobile-login routes
    // with the provider's id_token / access_token in the body and receive
    // a dmart session.
    // -------------------------------------------------------------------

    // POST /user/google/mobile-login — body: { token: "<id_token>" }.
    public Task<Response> GoogleMobileLoginAsync(string idToken, CancellationToken ct = default)
        => MobileLoginAsync("google", idToken, ct);

    // POST /user/facebook/mobile-login — body: { token: "<access_token>" }.
    public Task<Response> FacebookMobileLoginAsync(string accessToken, CancellationToken ct = default)
        => MobileLoginAsync("facebook", accessToken, ct);

    // POST /user/apple/mobile-login — body: { token: "<id_token>" }.
    public Task<Response> AppleMobileLoginAsync(string idToken, CancellationToken ct = default)
        => MobileLoginAsync("apple", idToken, ct);

    private async Task<Response> MobileLoginAsync(string provider, string token, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["token"] = token };
        using var req = BuildRequest(HttpMethod.Post,
            $"/user/{provider}/mobile-login", Json(body));
        var resp = await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
        // Capture the bearer token from the response, same way LoginAsync
        // does it, so subsequent calls on this DmartClient instance are
        // authenticated. Mobile-login intentionally tolerates a missing
        // token (the server may issue a different envelope shape on first
        // sign-up) so we ignore the bool.
        TryExtractAndStoreToken(resp);
        return resp;
    }

    // -------------------------------------------------------------------
    // Shared helpers (token capture + enum wire form)
    // -------------------------------------------------------------------

    // Single source of truth for "did the response carry a usable bearer
    // token, and if so, stash it on the client". Returns true when a token
    // was extracted and stored; callers that REQUIRE a token (LoginAsync,
    // LoginByAsync) translate a false result into a thrown DmartException;
    // callers that tolerate a missing token (MobileLoginAsync) ignore the
    // bool.
    internal bool TryExtractAndStoreToken(Response resp)
    {
        if (resp.Status != Status.Success || resp.Records is null || resp.Records.Count == 0) return false;
        var attrs = resp.Records[0].Attributes;
        if (attrs is null) return false;
        if (!attrs.TryGetValue("access_token", out var raw)) return false;
        var token = raw switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => null,
        };
        if (string.IsNullOrEmpty(token)) return false;
        _authToken = token;
        return true;
    }

    private static string QueryTypeWire(QueryType type)
    {
#if NET8_0_OR_GREATER
        return JsonSerializer.Serialize(type, DmartClientJsonContext.Default.QueryType).Trim('"');
#else
        return JsonSerializer.Serialize(type, DefaultJsonOptions).Trim('"');
#endif
    }
}
