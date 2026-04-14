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
        // Catchall {**rest} captures multi-segment subpaths AND the trailing shortname.
        // Mirrors dmart Python's `/entry/{resource_type}/{space}/{subpath:path}/{shortname}`.
        // Python's db.load() dispatches to different tables based on class_type:
        //   Space → spaces table, User → users table, Role → roles, Permission → permissions
        //   everything else → entries table
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
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Results.BadRequest();
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname)) return Results.BadRequest();

                var actor = http.User.Identity?.Name;

                // Route to the correct table based on resource_type.
                // Non-entry types are returned directly (no attachment support).
                switch (rt)
                {
                    case ResourceType.Space:
                    {
                        var s = await spaces.GetAsync(shortname, ct);
                        return s is null ? Results.NotFound() : Results.Json(s, DmartJsonContext.Default.Space);
                    }
                    case ResourceType.User:
                    {
                        var u = await users.GetByShortnameAsync(shortname, ct);
                        return u is null ? Results.NotFound() : Results.Json(u, DmartJsonContext.Default.User);
                    }
                    case ResourceType.Role:
                    {
                        var r = await access.GetRoleAsync(shortname, ct);
                        return r is null ? Results.NotFound() : Results.Json(r, DmartJsonContext.Default.Role);
                    }
                    case ResourceType.Permission:
                    {
                        var p = await access.GetPermissionAsync(shortname, ct);
                        return p is null ? Results.NotFound() : Results.Json(p, DmartJsonContext.Default.Permission);
                    }
                }

                // Entry-flavor types: load, convert to Record (matching Python's dict
                // response shape), and optionally attach child attachments.
                var entry = await svc.GetAsync(new Locator(rt, space, subpath, shortname), actor, ct);
                if (entry is null) return Results.NotFound();

                var record = EntryMapper.ToRecord(entry, space, retrieve_json_payload == true);

                // Python always includes "attachments" in the response (empty dict
                // when not requested or no children). Match that by defaulting to
                // an empty dictionary so the key is never omitted.
                Dictionary<string, List<Record>> attachmentsDict = new();

                if (retrieve_attachments == true)
                {
                    var children = await attachmentRepo.ListForParentAsync(space, subpath, shortname, ct);
                    if (children.Count > 0)
                    {
                        attachmentsDict = children
                            .GroupBy(a => JsonbHelpers.EnumMember(a.ResourceType))
                            .ToDictionary(
                                grp => grp.Key,
                                grp => grp.Select(a => AttachmentMapper.ToEntryRecord(a)).ToList());
                    }
                }

                record = record with { Attachments = attachmentsDict };
                return Results.Json(record, DmartJsonContext.Default.Record);
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
