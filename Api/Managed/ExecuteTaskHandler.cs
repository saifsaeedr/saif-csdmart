using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class ExecuteTaskHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // POST /managed/execute/{task_type}/{space_name}
        // dmart's only defined task_type is "query": load a saved query entry by
        // shortname (provided in the body or URL), parse its payload.body as a Query,
        // and execute via QueryService.
        g.MapPost("/execute/{task_type}/{space_name}",
            async (string task_type, string space_name, HttpRequest req,
                   EntryRepository entries, QueryService queries, HttpContext http,
                   CancellationToken ct) =>
            {
                if (!string.Equals(task_type, "query", StringComparison.OrdinalIgnoreCase))
                    return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                        $"unknown task type '{task_type}'", ErrorTypes.Request);

                Dictionary<string, object>? body = null;
                try
                {
                    body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
                }
                catch (JsonException) { /* tolerate empty body */ }

                var taskShortname = body?.TryGetValue("shortname", out var sn) == true ? sn?.ToString() : null;
                var taskSubpath = body?.TryGetValue("subpath", out var sp) == true ? sp?.ToString() ?? "/tasks" : "/tasks";
                var overrides = body?.TryGetValue("query_overrides", out var qo) == true && qo is JsonElement el ? el : (JsonElement?)null;

                if (string.IsNullOrEmpty(taskShortname))
                    return Response.Fail(InternalErrorCode.MISSING_DATA,
                        "task shortname required in body", ErrorTypes.Request);

                // dmart treats tasks as Content entries with a Query in payload.body.
                var taskEntry = await entries.GetAsync(space_name, taskSubpath, taskShortname, ResourceType.Content, ct);
                if (taskEntry?.Payload?.Body is null)
                    return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST,
                        $"task '{taskShortname}' not found at {space_name}{taskSubpath}", ErrorTypes.Request);

                Query? query;
                try
                {
                    var queryJson = JsonSerializer.Serialize(taskEntry.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
                    query = JsonSerializer.Deserialize(queryJson, DmartJsonContext.Default.Query);
                }
                catch (JsonException ex)
                {
                    return Response.Fail(InternalErrorCode.INVALID_DATA,
                        $"task body is not a valid Query: {ex.Message}", ErrorTypes.Request);
                }
                if (query is null)
                    return Response.Fail(InternalErrorCode.INVALID_DATA, "task body is empty", ErrorTypes.Request);

                // Apply caller-provided overrides (e.g. limit, offset, search) onto the
                // saved query before executing.
                if (overrides.HasValue && overrides.Value.ValueKind == JsonValueKind.Object)
                    query = ApplyOverrides(query, overrides.Value);

                return await queries.ExecuteAsync(query, http.Actor(), ct);
            });

        // Apply-alteration is wired in AlterationHandler.cs.
    }

    private static Query ApplyOverrides(Query original, JsonElement overrides)
    {
        // Build a merged query by walking the override fields and copying onto the
        // saved query. Limited to commonly-overridden fields to avoid surprises.
        var q = original;
        if (overrides.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var l)) q = q with { Limit = l };
        if (overrides.TryGetProperty("offset", out var offset) && offset.TryGetInt32(out var o)) q = q with { Offset = o };
        if (overrides.TryGetProperty("search", out var search) && search.ValueKind == JsonValueKind.String) q = q with { Search = search.GetString() };
        if (overrides.TryGetProperty("subpath", out var subpath) && subpath.ValueKind == JsonValueKind.String) q = q with { Subpath = subpath.GetString()! };
        return q;
    }
}
