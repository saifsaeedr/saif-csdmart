using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// Mirrors POST /managed/apply-alteration/{space}/{alteration_name}.
//
// dmart's "alteration" resource is a saved-instruction record. Its payload.body has
// the shape:
//   {
//     "target_query": { ...Query... },          // entries to modify
//     "patch":        { ...attributes patch... } // applied to each match
//   }
//
// Apply walks the query results, calls EntryService.UpdateAsync on each, and reports
// per-entry success/failure.
public static class AlterationHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/apply-alteration/{space}/{alteration_name}",
            async Task<Response> (string space, string alteration_name,
                                  EntryRepository entries, EntryService entryService,
                                  HttpContext http, CancellationToken ct) =>
            {
                var alteration = await entries.GetAsync(space, "/alterations", alteration_name, ResourceType.Alteration, ct);
                if (alteration is null)
                {
                    // dmart projects sometimes store alterations as Content under /alterations.
                    alteration = await entries.GetAsync(space, "/alterations", alteration_name, ResourceType.Content, ct);
                }
                if (alteration?.Payload?.Body is null)
                    return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST,
                        $"alteration '{alteration_name}' not found", ErrorTypes.Request);

                var bodyJson = JsonSerializer.Serialize(alteration.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
                using var doc = JsonDocument.Parse(bodyJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("target_query", out var qEl) || qEl.ValueKind != JsonValueKind.Object)
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "alteration body missing target_query", ErrorTypes.Request);
                if (!root.TryGetProperty("patch", out var patchEl) || patchEl.ValueKind != JsonValueKind.Object)
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "alteration body missing patch", ErrorTypes.Request);

                Query? query;
                try
                {
                    query = JsonSerializer.Deserialize(qEl.GetRawText(), DmartJsonContext.Default.Query);
                }
                catch (JsonException)
                {
                    return Response.Fail(InternalErrorCode.INVALID_DATA, "invalid request body", ErrorTypes.Request);
                }
                if (query is null)
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "target_query empty", ErrorTypes.Request);

                var patchDict = JsonElementToDict(patchEl);

                var matches = await entries.QueryAsync(query, ct);
                var updated = 0;
                var failed = new List<Dictionary<string, object>>();
                var actor = http.Actor();

                foreach (var entry in matches)
                {
                    var locator = new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname);
                    var result = await entryService.UpdateAsync(locator, patchDict, actor, ct);
                    if (result.IsOk) updated++;
                    else failed.Add(new()
                    {
                        ["shortname"] = entry.Shortname,
                        ["subpath"] = entry.Subpath,
                        ["error"] = result.ErrorMessage ?? "unknown",
                        ["code"] = result.ErrorCode,
                    });
                }

                return Response.Ok(attributes: new()
                {
                    ["alteration"] = alteration_name,
                    ["matched"] = matches.Count,
                    ["updated"] = updated,
                    ["failed_count"] = failed.Count,
                    ["failed"] = failed,
                });
            });

    // Convert a JsonElement object into a Dictionary<string, object> for the patch.
    // JSON null is represented as a JsonElement of ValueKind.Null so the dict
    // stays type-safe (no `null!` escape hatch) and ApplyPatch can detect the
    // intent to unset a field.
    private static Dictionary<string, object> JsonElementToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object>();
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? (object)"",
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : (object)prop.Value.GetDouble(),
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                // Pass JSON null through as the JsonElement itself — callers
                // already handle JsonElement with ValueKind.Null via FlattenAttrs.
                JsonValueKind.Null   => prop.Value.Clone(),
                _                    => prop.Value.Clone(),  // arrays/objects → JsonElement
            };
        }
        return dict;
    }
}
