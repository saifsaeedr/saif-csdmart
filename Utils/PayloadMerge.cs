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
        if (patchBody is null || patchBody.Value.ValueKind == JsonValueKind.Null)
            return existing;

        var basePayload = existing ?? new Payload { ContentType = ContentType.Json };
        return basePayload with
        {
            Body = JsonMerge.DeepMergeAndStripNulls(basePayload.Body, patchBody.Value),
        };
    }

    // Pull `payload.body` out of a request's `attributes["payload"]` value, which may
    // arrive as a JsonElement (source-gen JSON) or a Dictionary<string,object>.
    private static JsonElement? ExtractBody(object? payloadRaw)
    {
        if (payloadRaw is JsonElement pe && pe.ValueKind == JsonValueKind.Object
            && pe.TryGetProperty("body", out var bodyEl))
            return bodyEl;
        if (payloadRaw is Dictionary<string, object> pd && pd.TryGetValue("body", out var bodyObj)
            && bodyObj is JsonElement bje)
            return bje;
        return null;
    }
}
