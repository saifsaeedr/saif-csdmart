using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
#if NET8_0_OR_GREATER
using Dmart.Client.Json;
#endif

namespace Dmart.Client;

// High-level client for the dmart HTTP API — a C# analogue of pydmart's
// DmartService. One instance per base URL; the instance caches the bearer
// token issued at login and attaches it to every subsequent request. All
// methods are async and throw DmartException on any failure (transport,
// HTTP 4xx/5xx, or an envelope with status=failed).
//
// Threading: HttpClient is thread-safe for send operations. The token
// state is written once at login and read on every request — volatile
// read/write semantics are sufficient. Consumers who need separate tokens
// should construct separate DmartClient instances.
//
// Lifetime: DmartClient is Disposable only when it owns its HttpClient
// (the default constructor). If an HttpClient is supplied, the consumer
// owns it and must dispose it (the IHttpClientFactory pattern). Dispose
// is idempotent.
public sealed partial class DmartClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private volatile string? _authToken;
    private int _disposed;

    // JSON options used for every body serialize/deserialize. snake_case
    // matches dmart's wire convention and WhenWritingNull matches Python's
    // response_model_exclude_none=True behavior on the server side.
    public static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string BaseUrl { get; }

    // Current bearer token, null before login/after logout. Clients that
    // persist a token across process restarts can set it directly.
    public string? AuthToken
    {
        get => _authToken;
        set => _authToken = value;
    }

    // Default: construct a fresh HttpClient. For DI/typed-client scenarios,
    // pass an externally-managed instance and the caller retains ownership.
    public DmartClient(string baseUrl) : this(baseUrl, new HttpClient(), ownsHttp: true) { }

    public DmartClient(string baseUrl, HttpClient http)
        : this(baseUrl, http, ownsHttp: false) { }

    private DmartClient(string baseUrl, HttpClient http, bool ownsHttp)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("baseUrl must be a non-empty URL", nameof(baseUrl));
        BaseUrl = baseUrl.TrimEnd('/');
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = ownsHttp;
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsHttp) _http.Dispose();
    }

    // ============================================================
    // Core plumbing
    // ============================================================

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (!string.IsNullOrEmpty(_authToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        if (body is not null) req.Content = body;
        return req;
    }

    private async Task<Response> SendEnvelopeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DmartException(0, new Error("ClientError", 500, ex.Message, null));
        }

        Response? envelope;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
#if NET8_0_OR_GREATER
            envelope = await JsonSerializer
                .DeserializeAsync(stream, DmartClientJsonContext.Default.Response, ct)
                .ConfigureAwait(false);
#else
            envelope = await JsonSerializer
                .DeserializeAsync<Response>(stream, DefaultJsonOptions, ct)
                .ConfigureAwait(false);
#endif
        }
        catch (JsonException ex)
        {
            throw new DmartException((int)resp.StatusCode, new Error(
                ErrorTypes.Request, 500, $"unparsable response body: {ex.Message}", null));
        }

        if (envelope is null)
            throw new DmartException((int)resp.StatusCode, new Error(
                ErrorTypes.Request, 500, "empty response body", null));

        if (envelope.Status == Status.Failed)
            throw new DmartException((int)resp.StatusCode, envelope.Error ?? new Error(
                ErrorTypes.Request, 500, "failed without error payload", null));

        return envelope;
    }

    // Build a JsonContent body honoring the dmart wire convention
    // (SnakeCaseLower, omit-when-null). On net8.0+ we route through typed
    // overloads that use the source-gen context (AOT-safe); on
    // netstandard2.1 we fall back to reflection-based serialization via
    // DefaultJsonOptions — acceptable because AOT isn't available on that
    // TFM anyway.
#if NET8_0_OR_GREATER
    private static HttpContent Json(Request value)
        => JsonContent.Create(value, DmartClientJsonContext.Default.Request);
    private static HttpContent Json(Record value)
        => JsonContent.Create(value, DmartClientJsonContext.Default.Record);
    private static HttpContent Json(Query value)
        => JsonContent.Create(value, DmartClientJsonContext.Default.Query);
    private static HttpContent Json(Dictionary<string, object?> value)
        => JsonContent.Create(value!, DmartClientJsonContext.Default.DictionaryStringObject!);
#else
    private static HttpContent Json<T>(T value)
        => JsonContent.Create(value, options: DefaultJsonOptions);
#endif

    // ============================================================
    // Auth
    // ============================================================

    // POST /user/login — exchanges shortname + password for an access token.
    // On success the token is stored on the instance and attached to every
    // subsequent call. Mirrors pydmart.DmartService.login.
    public async Task<Response> LoginAsync(string shortname, string password, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["shortname"] = shortname,
            ["password"] = password,
        };
        using var req = BuildRequest(HttpMethod.Post, "/user/login", Json(body));
        var envelope = await SendEnvelopeAsync(req, ct).ConfigureAwait(false);

        // Python parity: login returns records[0].attributes.access_token.
        var token = envelope.Records?.FirstOrDefault()?.Attributes?.TryGetValue("access_token", out var t) == true
            ? (t is JsonElement el ? el.GetString() : t?.ToString())
            : null;
        if (string.IsNullOrEmpty(token))
            throw new DmartException(500, new Error(
                ErrorTypes.Request, 500, "login response missing access_token", null));
        _authToken = token;
        return envelope;
    }

    // POST /user/logout — invalidates the current session on the server.
    // Clears the local token regardless of outcome so a failed call doesn't
    // leave a stale token attached.
    public async Task<Response> LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Post, "/user/logout");
            return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
        }
        finally
        {
            _authToken = null;
        }
    }

    // ============================================================
    // Profile / identity
    // ============================================================

    // GET /user/profile — returns the current user record.
    public async Task<Response> GetProfileAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/user/profile");
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // ============================================================
    // Data
    // ============================================================

    // POST /{scope}/query — scope defaults to "managed" (requires auth).
    // Use scope="public" for anonymous / world-readable queries.
    public async Task<Response> QueryAsync(Query query, string scope = "managed", CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, $"/{scope}/query", Json(query));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /managed/request — unified CRUD envelope (create/update/patch/
    // delete/move/assign/update_acl). Server returns records[] on success
    // and error.info with per-record failures on partial failure.
    public async Task<Response> RequestAsync(Request action, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "/managed/request", Json(action));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // GET /{scope}/entry/{resource_type}/{space}/{subpath}/{shortname} —
    // fetches a single entry (or user/role/permission/space for those
    // resource types). `retrieveJsonPayload` / `retrieveAttachments` are
    // mapped to query-string flags matching the server.
    public async Task<JsonDocument> RetrieveEntryAsync(
        string resourceType, string spaceName, string subpath, string shortname,
        bool retrieveJsonPayload = false, bool retrieveAttachments = false,
        string scope = "managed", CancellationToken ct = default)
    {
        subpath = NormalizeSubpath(subpath);
        var path = $"/{scope}/entry/{resourceType}/{spaceName}{subpath}/{shortname}";
        var qs = new List<string>();
        if (retrieveJsonPayload) qs.Add("retrieve_json_payload=true");
        if (retrieveAttachments) qs.Add("retrieve_attachments=true");
        if (qs.Count > 0) path += "?" + string.Join("&", qs);

        using var req = BuildRequest(HttpMethod.Get, path);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex)
        {
            throw new DmartException(0, new Error("ClientError", 500, ex.Message, null));
        }

        // Entry endpoint returns a flat Meta-shaped object, not the Response
        // envelope, so parse as raw JsonDocument. Failure paths still return
        // the envelope shape — detect by probing the `status` property.
        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("status", out var statusProp)
            && statusProp.ValueKind == JsonValueKind.String
            && statusProp.GetString() == "failed"
            && doc.RootElement.TryGetProperty("error", out var errProp))
        {
#if NET8_0_OR_GREATER
            var err = JsonSerializer.Deserialize(errProp.GetRawText(), DmartClientJsonContext.Default.Error)
                ?? new Error(ErrorTypes.Request, 500, "unknown error", null);
#else
            var err = JsonSerializer.Deserialize<Error>(errProp.GetRawText(), DefaultJsonOptions)
                ?? new Error(ErrorTypes.Request, 500, "unknown error", null);
#endif
            doc.Dispose();
            throw new DmartException((int)resp.StatusCode, err);
        }
        return doc;
    }

    // POST /managed/resource_with_payload — multipart upload: record JSON +
    // payload bytes. Matches pydmart.upload_with_payload.
    public async Task<Response> UploadWithPayloadAsync(
        string spaceName, Record record, byte[] payloadBytes, string payloadFileName,
        string payloadMime = "application/octet-stream", string? sha = null,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(spaceName), "space_name");
        if (!string.IsNullOrEmpty(sha)) form.Add(new StringContent(sha), "sha");

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

        using var req = BuildRequest(HttpMethod.Post, "/managed/resource_with_payload", form);
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // ============================================================
    // Helpers
    // ============================================================

    // dmart expects subpaths with a leading slash and no trailing slash
    // (except the root "/"). Passing "users" or "/users/" should both
    // resolve to "/users" on the wire.
    private static string NormalizeSubpath(string subpath)
    {
        if (string.IsNullOrEmpty(subpath) || subpath == "/") return "/";
        var s = subpath.Trim('/');
        return "/" + s;
    }
}
