using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Dmart.Api.Mcp;

// Handler implementations for every MCP tool. Each handler:
//   1. Reads and validates its arguments from the `JsonElement?` params.
//   2. Resolves services it needs from `http.RequestServices`.
//   3. Uses `http.User.Identity?.Name` as the actor so downstream services
//      enforce the caller's permissions (identical to how ProfileHandler and
//      every other authenticated endpoint works).
//   4. Builds a JSON result and returns it as a JsonElement.
//
// Handlers may throw — McpEndpoint.HandleToolCall catches and surfaces errors
// via MCP's ToolsCallResult.IsError convention (not as JSON-RPC errors).
public static class McpTools
{
    // Hard cap on query results — prevents a runaway `dmart.query` from
    // eating the model's context window regardless of what `limit` it asks
    // for. dmart's own MaxQueryLimit still applies on top.
    private const int MaxQueryLimit = 50;

    // ---- dmart.me ----

    public static async Task<JsonElement> MeAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        var actor = RequireActor(http);
        var services = http.RequestServices;
        var svc = services.GetRequiredService<UserService>();
        var access = services.GetRequiredService<AccessRepository>();

        var user = await svc.GetByShortnameAsync(actor, ct)
            ?? throw new InvalidOperationException("user missing");
        var permissions = await access.GenerateUserPermissionsAsync(actor, ct);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("shortname", user.Shortname);
            if (!string.IsNullOrEmpty(user.Email)) w.WriteString("email", user.Email);
            if (!string.IsNullOrEmpty(user.Msisdn)) w.WriteString("msisdn", user.Msisdn);
            w.WriteString("type", user.Type.ToString().ToLowerInvariant());
            w.WriteString("language", user.Language.ToString().ToLowerInvariant());
            w.WriteBoolean("is_email_verified", user.IsEmailVerified);
            w.WriteBoolean("is_msisdn_verified", user.IsMsisdnVerified);
            WriteStringArray(w, "roles", user.Roles);
            WriteStringArray(w, "groups", user.Groups);
            // Permissions: list the keys only (space:subpath:resource_type) so
            // the LLM gets a concise picture of what's accessible without
            // flooding the context. The model can drill in with other tools.
            w.WriteStartArray("accessible");
            foreach (var key in permissions.Keys) w.WriteStringValue(key);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ParseBytes(ms.ToArray());
    }

    // ---- dmart.spaces ----

    public static async Task<JsonElement> SpacesAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        var actor = RequireActor(http);
        var qs = http.RequestServices.GetRequiredService<QueryService>();
        var q = new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = MaxQueryLimit,
        };
        var resp = await qs.ExecuteAsync(q, actor, ct);
        return SerializeResponse(resp);
    }

    // ---- dmart.query ----

    public static async Task<JsonElement> QueryAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;

        var spaceName = GetRequiredString(args, "space_name");
        var subpath = GetString(args, "subpath") ?? "/";
        var search = GetString(args, "search");
        var queryType = TryParseEnum<QueryType>(GetString(args, "type")) ?? QueryType.Search;

        List<ResourceType>? filterTypes = null;
        if (args.TryGetProperty("resource_types", out var rtArr) &&
            rtArr.ValueKind == JsonValueKind.Array)
        {
            filterTypes = [];
            foreach (var el in rtArr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String &&
                    TryParseEnum<ResourceType>(el.GetString()) is ResourceType rt)
                {
                    filterTypes.Add(rt);
                }
            }
        }

        List<string>? filterShortnames = null;
        if (args.TryGetProperty("filter_shortnames", out var snArr) &&
            snArr.ValueKind == JsonValueKind.Array)
        {
            filterShortnames = [];
            foreach (var el in snArr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) filterShortnames.Add(el.GetString()!);
        }

        var rawLimit = GetInt(args, "limit") ?? 20;
        var limit = Math.Min(Math.Max(1, rawLimit), MaxQueryLimit);

        var q = new Query
        {
            Type = queryType,
            SpaceName = spaceName,
            Subpath = subpath,
            Search = search,
            FilterTypes = filterTypes,
            FilterShortnames = filterShortnames,
            Limit = limit,
        };

        var qs = http.RequestServices.GetRequiredService<QueryService>();
        var resp = await qs.ExecuteAsync(q, RequireActor(http), ct);
        return SerializeResponse(resp);
    }

    // ---- dmart.read ----

    public static async Task<JsonElement> ReadAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;

        var space = GetRequiredString(args, "space_name");
        var subpath = GetString(args, "subpath") ?? "/";
        var shortname = GetRequiredString(args, "shortname");
        var resourceType = TryParseEnum<ResourceType>(GetString(args, "resource_type"))
            ?? ResourceType.Content;

        // Reuse the Query path so dmart's permission resolver runs exactly
        // once and returns exactly what the user can see (or nothing).
        // FilterShortnames + FilterTypes narrows the scan to the target
        // entry; the resulting Response.Records carries the entry with
        // permissions already applied.
        var q = new Query
        {
            Type = QueryType.Search,
            SpaceName = space,
            Subpath = subpath,
            FilterShortnames = [shortname],
            FilterTypes = [resourceType],
            RetrieveJsonPayload = true,
            Limit = 1,
        };
        var qs = http.RequestServices.GetRequiredService<QueryService>();
        var resp = await qs.ExecuteAsync(q, RequireActor(http), ct);
        return SerializeResponse(resp);
    }

    // ---- dmart.schema ----

    public static async Task<JsonElement> SchemaAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;

        var space = GetRequiredString(args, "space_name");
        var shortname = GetRequiredString(args, "shortname");

        // Schemas conventionally live under /schema in every space.
        var q = new Query
        {
            Type = QueryType.Search,
            SpaceName = space,
            Subpath = "/schema",
            FilterShortnames = [shortname],
            FilterTypes = [ResourceType.Schema],
            RetrieveJsonPayload = true,
            Limit = 1,
        };
        var qs = http.RequestServices.GetRequiredService<QueryService>();
        var resp = await qs.ExecuteAsync(q, RequireActor(http), ct);
        return SerializeResponse(resp);
    }

    // ---- dmart.create ----

    public static async Task<JsonElement> CreateAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;
        var actor = RequireActor(http);

        var space = GetRequiredString(args, "space_name");
        var subpath = GetString(args, "subpath") ?? "/";
        var shortname = GetRequiredString(args, "shortname");
        var resourceType = TryParseEnum<ResourceType>(GetString(args, "resource_type"))
            ?? throw new ArgumentException("resource_type required and must be a valid enum value");

        var now = DateTime.UtcNow;
        Payload? payload = null;
        if (args.TryGetProperty("payload", out var payloadEl) &&
            payloadEl.ValueKind == JsonValueKind.Object)
        {
            payload = new Payload
            {
                ContentType = ContentType.Json,
                SchemaShortname = GetString(args, "schema_shortname"),
                Body = payloadEl.Clone(),
            };
        }

        var entry = new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = resourceType,
            OwnerShortname = actor,
            IsActive = true,
            Payload = payload,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var result = await http.RequestServices.GetRequiredService<EntryService>()
            .CreateAsync(entry, actor, ct);
        if (!result.IsOk)
            throw new InvalidOperationException(result.ErrorMessage ?? "create failed");

        var created = result.Value!;
        return BuildEntryRef(created.SpaceName, created.Subpath, created.Shortname,
            created.ResourceType, created.Uuid, "created");
    }

    // ---- dmart.update ----

    public static async Task<JsonElement> UpdateAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;
        var actor = RequireActor(http);

        var space = GetRequiredString(args, "space_name");
        var subpath = GetString(args, "subpath") ?? "/";
        var shortname = GetRequiredString(args, "shortname");
        var resourceType = TryParseEnum<ResourceType>(GetString(args, "resource_type"))
            ?? ResourceType.Content;

        if (!args.TryGetProperty("patch", out var patchEl) ||
            patchEl.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("`patch` object required");

        // Convert JsonElement patch → Dictionary<string, object> the
        // EntryService expects. Nested values stay as JsonElement so the
        // service layer round-trips them losslessly.
        var patch = new Dictionary<string, object>();
        foreach (var prop in patchEl.EnumerateObject())
            patch[prop.Name] = prop.Value.Clone();

        var locator = new Locator(resourceType, space, subpath, shortname);
        var result = await http.RequestServices.GetRequiredService<EntryService>()
            .UpdateAsync(locator, patch, actor, ct);
        if (!result.IsOk)
            throw new InvalidOperationException(result.ErrorMessage ?? "update failed");

        var updated = result.Value!;
        return BuildEntryRef(updated.SpaceName, updated.Subpath, updated.Shortname,
            updated.ResourceType, updated.Uuid, "updated");
    }

    // ---- dmart.delete ----
    //
    // Destructive. Requires an explicit `confirm: true` argument — without
    // it, the tool rejects up-front. This is a lightweight guard pending
    // MCP elicitation support becoming universal across clients; once every
    // major client handles `elicitation/create`, v0.5 switches this to a
    // server-initiated confirmation prompt over SSE.

    public static async Task<JsonElement> DeleteAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;
        var actor = RequireActor(http);

        var confirm = args.TryGetProperty("confirm", out var confEl)
            && confEl.ValueKind == JsonValueKind.True;
        if (!confirm)
            throw new ArgumentException(
                "delete requires explicit `confirm: true` — ask the user first, then retry");

        var space = GetRequiredString(args, "space_name");
        var subpath = GetString(args, "subpath") ?? "/";
        var shortname = GetRequiredString(args, "shortname");
        var resourceType = TryParseEnum<ResourceType>(GetString(args, "resource_type"))
            ?? ResourceType.Content;

        var locator = new Locator(resourceType, space, subpath, shortname);
        var result = await http.RequestServices.GetRequiredService<EntryService>()
            .DeleteAsync(locator, actor, ct);
        if (!result.IsOk)
            throw new InvalidOperationException(result.ErrorMessage ?? "delete failed");

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("status", result.Value ? "deleted" : "not-found");
            w.WriteString("space_name", space);
            w.WriteString("subpath", subpath);
            w.WriteString("shortname", shortname);
            w.WriteString("resource_type", resourceType.ToString().ToLowerInvariant());
            w.WriteEndObject();
        }
        return ParseBytes(ms.ToArray());
    }

    // ---- dmart.semantic_search ----

    public static async Task<JsonElement> SemanticSearchAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;
        var actor = RequireActor(http);

        var query = GetRequiredString(args, "query");
        var space = GetString(args, "space_name");
        var subpath = GetString(args, "subpath");
        var rawLimit = GetInt(args, "limit") ?? 10;
        var limit = Math.Min(Math.Max(1, rawLimit), MaxQueryLimit);

        List<ResourceType>? types = null;
        if (args.TryGetProperty("resource_types", out var rtArr) &&
            rtArr.ValueKind == JsonValueKind.Array)
        {
            types = [];
            foreach (var el in rtArr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String &&
                    TryParseEnum<ResourceType>(el.GetString()) is ResourceType rt)
                    types.Add(rt);
            }
        }

        var svc = http.RequestServices.GetRequiredService<SemanticSearchService>();
        var resp = await svc.SearchAsync(query, space, subpath, types, limit, actor, ct);
        return SerializeResponse(resp);
    }

    // ---- dmart.history ----

    public static async Task<JsonElement> HistoryAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;
        var actor = RequireActor(http);

        var space = GetRequiredString(args, "space_name");
        var subpath = GetString(args, "subpath") ?? "/";
        var shortname = GetRequiredString(args, "shortname");
        var rawLimit = GetInt(args, "limit") ?? 20;
        var limit = Math.Min(Math.Max(1, rawLimit), MaxQueryLimit);

        var q = new Query
        {
            Type = QueryType.History,
            SpaceName = space,
            Subpath = subpath,
            FilterShortnames = [shortname],
            Limit = limit,
        };
        var qs = http.RequestServices.GetRequiredService<QueryService>();
        var resp = await qs.ExecuteAsync(q, actor, ct);
        return SerializeResponse(resp);
    }

    // ---- dmart.download ----
    //
    // Fetches the bytes/text of a payload-bearing resource. Two cases:
    //
    //   * Attachment-flavor (Media, Comment, Reply, Reaction, Json, Share,
    //     Relationship, Alteration, Lock, DataAsset) → AttachmentRepository
    //     loads the blob from the `attachments.media` column.
    //   * Entry-flavor (Content, Folder, Schema, Ticket, …) → EntryService
    //     loads the entry, and we return `payload.body` (inline JSON).
    //
    // Hard 5 MB size cap — MCP text content would blow out the model's
    // context window above that. Binary content is returned as base64;
    // text/* and application/json come through as plain text.

    private const int MaxDownloadBytes = 5 * 1024 * 1024;  // 5 MB

    public static async Task<JsonElement> DownloadAsync(
        JsonElement? arguments, HttpContext http, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments object required");
        var args = arguments.Value;
        var actor = RequireActor(http);

        var space = GetRequiredString(args, "space_name");
        var subpath = GetString(args, "subpath") ?? "/";
        var shortname = GetRequiredString(args, "shortname");
        var resourceType = TryParseEnum<ResourceType>(GetString(args, "resource_type"))
            ?? ResourceType.Content;

        // Explicit permission check — CanReadAsync walks the same
        // user→role→permission chain the HTTP handlers rely on.
        var services = http.RequestServices;
        var perms = services.GetRequiredService<PermissionService>();
        var locator = new Locator(resourceType, space, subpath, shortname);
        if (!await perms.CanReadAsync(actor, locator, ct))
            throw new UnauthorizedAccessException("no read access");

        // Attachment-flavor: load bytes from the attachments table.
        if (Api.Managed.ResourceWithPayloadHandler.IsAttachmentResourceType(resourceType))
        {
            var repo = services.GetRequiredService<AttachmentRepository>();
            var att = await repo.GetAsync(space, subpath, shortname, ct);
            if (att is null)
                throw new InvalidOperationException("attachment not found");

            // Prefer binary media; fall back to text body.
            if (att.Media is not null)
            {
                if (att.Media.Length > MaxDownloadBytes)
                    throw new InvalidOperationException(
                        $"attachment exceeds {MaxDownloadBytes / (1024 * 1024)}MB download cap");
                var mime = Api.Managed.PayloadHandler.MimeFor(att.Payload?.ContentType, "");
                return BuildDownloadResponse(space, subpath, shortname, resourceType,
                    mime, bytes: att.Media, text: null);
            }
            if (att.Body is not null)
            {
                if (att.Body.Length > MaxDownloadBytes)
                    throw new InvalidOperationException(
                        $"attachment body exceeds {MaxDownloadBytes / (1024 * 1024)}MB download cap");
                return BuildDownloadResponse(space, subpath, shortname, resourceType,
                    mime: "text/plain", bytes: null, text: att.Body);
            }
            throw new InvalidOperationException("attachment has no payload");
        }

        // Entry-flavor: inline JSON payload.
        var entries = services.GetRequiredService<EntryService>();
        var entry = await entries.GetAsync(locator, actor, ct)
            ?? throw new InvalidOperationException("entry not found");
        if (entry.Payload?.Body is null)
            throw new InvalidOperationException("entry has no payload body");

        var bodyJson = JsonSerializer.Serialize(
            entry.Payload.Body.Value, DmartJsonContext.Default.JsonElement);
        if (bodyJson.Length > MaxDownloadBytes)
            throw new InvalidOperationException(
                $"payload exceeds {MaxDownloadBytes / (1024 * 1024)}MB download cap");
        return BuildDownloadResponse(space, subpath, shortname, resourceType,
            mime: "application/json", bytes: null, text: bodyJson);
    }

    // Wraps the download result in a compact envelope the LLM can interpret:
    //   { mime, size, encoding: "utf8"|"base64", content: "..." }
    // For text content (text/*, application/json), `content` is the raw
    // text. For binary, `content` is base64 and `encoding` is "base64".
    private static JsonElement BuildDownloadResponse(
        string space, string subpath, string shortname, ResourceType rt,
        string mime, byte[]? bytes, string? text)
    {
        var isText = text is not null ||
            mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mime, "application/json", StringComparison.OrdinalIgnoreCase);

        string encoding;
        string content;
        int size;
        if (text is not null)
        {
            encoding = "utf8";
            content = text;
            size = System.Text.Encoding.UTF8.GetByteCount(text);
        }
        else if (bytes is not null && isText)
        {
            encoding = "utf8";
            content = System.Text.Encoding.UTF8.GetString(bytes);
            size = bytes.Length;
        }
        else
        {
            encoding = "base64";
            content = Convert.ToBase64String(bytes ?? Array.Empty<byte>());
            size = bytes?.Length ?? 0;
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("space_name", space);
            w.WriteString("subpath", subpath);
            w.WriteString("shortname", shortname);
            w.WriteString("resource_type", rt.ToString().ToLowerInvariant());
            w.WriteString("mime", mime);
            w.WriteNumber("size", size);
            w.WriteString("encoding", encoding);
            w.WriteString("content", content);
            w.WriteEndObject();
        }
        return ParseBytes(ms.ToArray());
    }

    // ---- helpers ----

    // Compact reply after create/update so the model gets a recognizable
    // receipt without us round-tripping the full entry shape.
    private static JsonElement BuildEntryRef(
        string space, string subpath, string shortname,
        ResourceType rt, string uuid, string status)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("status", status);
            w.WriteString("space_name", space);
            w.WriteString("subpath", subpath);
            w.WriteString("shortname", shortname);
            w.WriteString("resource_type", rt.ToString().ToLowerInvariant());
            w.WriteString("uuid", uuid);
            w.WriteString("uri", $"dmart://{space}{(subpath == "/" ? "" : subpath)}/{shortname}");
            w.WriteEndObject();
        }
        return ParseBytes(ms.ToArray());
    }

    private static string RequireActor(HttpContext http) =>
        http.User.Identity?.Name
            ?? throw new UnauthorizedAccessException("login required");

    private static JsonElement SerializeResponse(Response resp)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(resp, DmartJsonContext.Default.Response);
        return ParseBytes(bytes);
    }

    private static JsonElement ParseBytes(byte[] bytes)
        => JsonDocument.Parse(bytes).RootElement.Clone();

    private static string GetRequiredString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"missing or non-string property: {name}");
        var s = el.GetString();
        if (string.IsNullOrEmpty(s))
            throw new ArgumentException($"property must be non-empty: {name}");
        return s;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i;
        return null;
    }

    private static T? TryParseEnum<T>(string? s) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(s)) return null;
        return Enum.TryParse<T>(s, ignoreCase: true, out var v) ? v : null;
    }

    private static void WriteStringArray(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}
