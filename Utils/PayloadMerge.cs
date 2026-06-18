using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Utils;

// Single source of truth for applying a request's `payload.body` to an existing
// resource on update/patch. Mirrors Python's Meta.update_from_record →
// Payload.update(replace=false): the patch body is DEEP-MERGED into the existing
// body (a property sent as null removes the key), never replaced wholesale.
//
// Used by every resource-update path so the merge logic can't diverge per branch
// (which is exactly what let the managed user-update path silently replace bodies):
//   - Api/Managed/RequestHandler.cs  (managed user update)
//   - Services/EntryService.cs       (entry update)
//   - Services/UserService.cs        (self-service /user/profile)
public static class PayloadMerge
{
    // Returns `existing` unchanged when the request carries no usable payload body
    // (absent, or an explicit JSON null) — Python's Payload.update returns None /
    // no-ops when `body` is None. Otherwise deep-merges the patch body into the
    // existing body, creating a default JSON Payload when the resource had none.
    public static Payload? MergeBody(Payload? existing, object? payloadRaw)
    {
        var patchBody = ExtractBody(payloadRaw);
        var hasBody = patchBody is not null && patchBody.Value.ValueKind != JsonValueKind.Null;
        // The schema/content-type the patch DECLARES, if any. Honoring these is
        // what makes serve_request_update's schema gate meaningful: an update that
        // (re)declares schema_shortname must validate the merged body against it,
        // and the declared schema must be the one persisted. Body-only patches omit
        // these, so we fall back to the existing values rather than clearing them.
        var patchSchema = ExtractString(payloadRaw, "schema_shortname");
        var patchContentType = ExtractString(payloadRaw, "content_type");

        // Nothing usable in the patch payload (no body, no metadata) → leave the
        // resource untouched, matching Python's Payload.update no-op on empty input.
        if (!hasBody && patchSchema is null && patchContentType is null)
            return existing;

        var basePayload = existing ?? new Payload { ContentType = ContentType.Json };
        return basePayload with
        {
            Body = hasBody
                ? JsonMerge.DeepMergeAndStripNulls(basePayload.Body, patchBody!.Value)
                : basePayload.Body,
            SchemaShortname = patchSchema ?? basePayload.SchemaShortname,
            ContentType = patchContentType is not null
                ? ParseContentType(patchContentType)
                : basePayload.ContentType,
        };
    }

    // Single source of truth for the wire `content_type` token → ContentType enum
    // mapping. Lowercase tokens ("json"/"text"/"markdown"/"html", and any other
    // enum member) map case-insensitively; null or anything unrecognized defaults
    // to JSON. Shared by the create path (RequestHandler.ParsePayloadFromAttrs) and
    // the update path (MergeBody) so the two can't disagree.
    internal static ContentType ParseContentType(string? value)
        => value is not null && Enum.TryParse<ContentType>(value, ignoreCase: true, out var ct)
            ? ct : ContentType.Json;

    // Pull `payload.body` out of a request's `attributes["payload"]` value, which may
    // arrive as a JsonElement (source-gen JSON) or a Dictionary<string,object>.
    // internal so the /user/profile protected-fields check screens the exact same
    // body shapes this helper would later merge — no JsonElement-only blind spot.
    internal static JsonElement? ExtractBody(object? payloadRaw)
    {
        if (payloadRaw is JsonElement pe && pe.ValueKind == JsonValueKind.Object
            && pe.TryGetProperty("body", out var bodyEl))
            return bodyEl;
        if (payloadRaw is Dictionary<string, object> pd && pd.TryGetValue("body", out var bodyObj)
            && bodyObj is JsonElement bje)
            return bje;
        return null;
    }

    // Pull a string-valued payload field (schema_shortname / content_type) out of
    // the request's `attributes["payload"]` value, handling the same JsonElement
    // and Dictionary<string,object> shapes as ExtractBody. Returns null when the
    // field is absent or not a JSON string.
    private static string? ExtractString(object? payloadRaw, string property)
    {
        if (payloadRaw is JsonElement pe && pe.ValueKind == JsonValueKind.Object
            && pe.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        if (payloadRaw is Dictionary<string, object> pd && pd.TryGetValue(property, out var obj))
        {
            if (obj is string s) return s;
            if (obj is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString();
        }
        return null;
    }
}
