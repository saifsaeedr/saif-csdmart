using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class EntryHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Mirrors dmart Python's `/entry/{resource_type}/{space}/{subpath:path}/{shortname}`.
        // Python returns {**meta.model_dump(exclude_none=True), "attachments": {...}}
        // — Meta fields at root (no space_name/subpath/resource_type), attachments
        // use Record shape with attributes wrapper.
        //
        // Query parameters (matching Python):
        //   retrieve_json_payload   — include payload.body in the response (default false)
        //   retrieve_attachments    — include child attachments grouped by type (default false)
        g.MapGet("/entry/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   bool? retrieve_json_payload,
                   bool? retrieve_attachments,
                   EntryService svc,
                   AttachmentRepository attachmentRepo,
                   SpaceRepository spaces,
                   UserRepository users,
                   AccessRepository access,
                   HttpContext http, CancellationToken ct) =>
            {
                // Python parity: every failure path returns the structured
                // {status:"failed", error:{type, code, message}} envelope. The
                // caller's Python client decodes api.Error — bare 404/400 HTML
                // breaks that contract. Not-found still uses HTTP 404 so
                // clients can branch on the status code without parsing the
                // body (matches Python api.Error bindings + existing tests).
                static IResult NotFoundMedia() => Results.Json(
                    Response.Fail(InternalErrorCode.OBJECT_NOT_FOUND,
                        "Request object is not available", ErrorTypes.Media),
                    DmartJsonContext.Default.Response, statusCode: 404);

                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Results.Json(
                        Response.Fail(InternalErrorCode.INVALID_DATA,
                            $"invalid resource_type '{resource_type}'", ErrorTypes.Request),
                        DmartJsonContext.Default.Response, statusCode: 400);
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname))
                    return Results.Json(
                        Response.Fail(InternalErrorCode.INVALID_DATA,
                            "shortname required", ErrorTypes.Request),
                        DmartJsonContext.Default.Response, statusCode: 400);

                var actor = http.Actor();

                // Non-entry types: direct serialization.
                switch (rt)
                {
                    case ResourceType.Space:
                    {
                        var s = await spaces.GetAsync(shortname, ct);
                        return s is null ? NotFoundMedia() : Results.Json(s, DmartJsonContext.Default.Space);
                    }
                    case ResourceType.User:
                    {
                        var u = await users.GetByShortnameAsync(shortname, ct);
                        return u is null ? NotFoundMedia() : Results.Json(u, DmartJsonContext.Default.User);
                    }
                    case ResourceType.Role:
                    {
                        var r = await access.GetRoleAsync(shortname, ct);
                        return r is null ? NotFoundMedia() : Results.Json(r, DmartJsonContext.Default.Role);
                    }
                    case ResourceType.Permission:
                    {
                        var p = await access.GetPermissionAsync(shortname, ct);
                        return p is null ? NotFoundMedia() : Results.Json(p, DmartJsonContext.Default.Permission);
                    }
                }

                var entry = await svc.GetAsync(new Locator(rt, space, subpath, shortname), actor, ct);
                if (entry is null) return NotFoundMedia();

                // Build attachments grouped by resource_type. Each attachment
                // keeps its `attributes` wrapper around the meta fields, to
                // match Python's `get_entry_attachments` shape (adapter.py:
                // 1342 — `attachment["attributes"] = {...}` — and the /entry
                // handler's `return {**meta.model_dump(), "attachments":
                // attachments}` composition at router.py:1003). Previously we
                // spread the attributes at the record root, which made every
                // attachment flat — clients parsing attachment.attributes.X
                // got `undefined`.
                var attNode = new JsonObject();
                if (retrieve_attachments == true)
                {
                    var children = await attachmentRepo.ListForParentAsync(space, subpath, shortname, ct);
                    foreach (var grp in children.GroupBy(a => JsonbHelpers.EnumMember(a.ResourceType)))
                    {
                        var arr = new JsonArray();
                        foreach (var rec in grp.Select(a => AttachmentMapper.ToEntryRecord(a)))
                        {
                            var recJson = JsonSerializer.Serialize(rec, DmartJsonContext.Default.Record);
                            arr.Add(JsonNode.Parse(recJson));
                        }
                        attNode[grp.Key] = arr;
                    }
                }

                var node = EntryToJsonNode.Convert(entry, retrieve_json_payload == true);
                node["attachments"] = attNode;
                return Results.Content(node.ToJsonString(DmartJsonContext.Default.Options), "application/json");
            });

        g.MapGet("/byuuid/{uuid}", async (string uuid, EntryService svc, CancellationToken ct) =>
        {
            if (!Guid.TryParse(uuid, out var u)) return Results.BadRequest();
            var entry = await svc.GetByUuidAsync(u, ct);
            return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
        });

        g.MapGet("/byslug/{slug}", async (string slug, EntryService svc, CancellationToken ct) =>
        {
            var entry = await svc.GetBySlugAsync(slug, ct);
            return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
        });
    }
}

/// <summary>
/// Serializes an Entry to a JsonNode (mutable JSON DOM) using the source-gen context,
/// then strips the payload body if not requested. This avoids Dictionary&lt;string, object&gt;
/// serialization issues with AOT while producing the flat response Python returns.
/// </summary>
internal static class EntryToJsonNode
{
    public static JsonNode Convert(Entry entry, bool includePayloadBody)
    {
        // Serialize via source-gen → guaranteed correct for all nested types.
        // SerializeToNode skips the string encode→parse round-trip that we
        // used to run here (Serialize + JsonNode.Parse) — same output, one pass.
        var node = JsonSerializer.SerializeToNode(entry, DmartJsonContext.Default.Entry)!.AsObject();

        // Remove fields that Python's Meta.model_dump() doesn't include.
        // These live on the DB row / Locator, not on the Meta model.
        node.Remove("query_policies");
        node.Remove("space_name");
        node.Remove("subpath");
        node.Remove("resource_type");

        // Strip payload.body if not requested.
        if (!includePayloadBody && node["payload"] is JsonObject payload)
            payload.Remove("body");

        return node;
    }
}
