using Dmart.Api.Managed;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Services;

namespace Dmart.Api.Public;

// Anonymous (no JWT) variants of the multipart upload endpoints. They share the
// implementation in Managed.ResourceWithPayloadHandler — only the actor differs.
public static class AttachHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/resource_with_payload",
            async Task<Response> (HttpRequest req, EntryService entries,
                                  AttachmentRepository attachments,
                                  ILogger<ResourceWithPayloadMarker> log, CancellationToken ct) =>
                await ResourceWithPayloadHandler.HandleAsync(req, entries, attachments, "anonymous", log, ct))
          .DisableAntiforgery();

        g.MapPost("/attach/{space_name}",
            async Task<Response> (string space_name, HttpRequest req, EntryService entries,
                                  AttachmentRepository attachments,
                                  ILogger<ResourceWithPayloadMarker> log, CancellationToken ct) =>
                await ResourceWithPayloadHandler.HandleAsync(req, entries, attachments, "anonymous", log, ct))
          .DisableAntiforgery();
    }
}
