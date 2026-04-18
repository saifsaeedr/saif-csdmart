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

    // ---- helpers ----

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
