using Dmart.Api;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Public;

public static class EntryHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/entry/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   bool? retrieve_json_payload,
                   bool? retrieve_attachments,
                   EntryService svc, AttachmentRepository attachmentRepo,
                   CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt)) return Results.BadRequest();
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname)) return Results.BadRequest();
                var entry = await svc.GetAsync(new Locator(rt, space, subpath, shortname), actor: null, ct);
                if (entry is null) return Results.NotFound();

                var record = EntryMapper.ToRecord(entry, space, retrieve_json_payload == true);

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
