using System.Text;
using System.Text.Json;
using Dmart.Api;
using MediaTypeNames = System.Net.Mime.MediaTypeNames;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// Serves the binary or JSON payload of a resource. dmart's storage convention:
//   * Attachment-flavor types (Comment, Reply, Reaction, Media, Json, Share, Lock,
//     DataAsset, Relationship, Alteration) → bytes live in attachments.media
//   * Entry-flavor types (Content, Folder, Schema, Ticket) → JSON payload lives
//     in entries.payload.body (jsonb)
public static class PayloadHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Catchall captures multi-segment subpath + filename. The filename is then
        // parsed by RouteParts.SplitPayloadParts to identify shortname / schema / ext.
        // Mirrors dmart Python's `/payload/{resource_type}/{space}/{subpath:path}/{shortname}.{ext}`
        // and `/payload/{resource_type}/{space}/{subpath:path}/{shortname}.{schema}.{ext}`.
        g.MapGet("/payload/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   AttachmentRepository attachments, EntryService entries,
                   PermissionService perms, HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Results.BadRequest($"unknown resource_type '{resource_type}'");
                var parts = RouteParts.SplitPayloadParts(rest);
                if (parts is null)
                    return Results.BadRequest($"invalid payload path '{rest}' — expected {{subpath}}/{{shortname}}.{{ext}}");
                var (subpath, shortname, _schema, ext) = parts.Value;
                return await ServePayloadAsync(rt, space, subpath, shortname, ext,
                    attachments, entries, perms, http.Actor(), ct);
            });
    }

    public static async Task<IResult> ServePayloadAsync(
        ResourceType rt, string space, string subpath, string shortname, string ext,
        AttachmentRepository attachments, EntryService entries, PermissionService perms,
        string? actor, CancellationToken ct)
    {
        var normalizedSubpath = Locator.NormalizeSubpath(subpath);

        if (ResourceWithPayloadHandler.IsAttachmentResourceType(rt))
        {
            var att = await attachments.GetAsync(space, normalizedSubpath, shortname, ct);
            if (att?.Media is null) return Results.NotFound();
            var attachmentLocator = new Locator(rt, space, normalizedSubpath, shortname);
            if (!await perms.CanReadAsync(actor, attachmentLocator, PermissionService.FromAttachment(att), ct))
            {
                return Results.Json(
                    Response.Fail(InternalErrorCode.NOT_ALLOWED,
                        "You don't have permission to this action", ErrorTypes.Request),
                    DmartJsonContext.Default.Response,
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            var mime = MimeFor(att.Payload?.ContentType, ext);
            // JSON payloads (either by stored content-type or by ".json" URL
            // suffix, case-insensitive) come back inline so callers can read
            // the body without a Content-Disposition: attachment forcing a
            // download in browsers. Everything else keeps the
            // filename-bearing attachment header used for media downloads.
            if (IsJsonResponse(mime, ext))
                return Results.File(att.Media, "application/json; charset=utf-8");
            return Results.File(att.Media, mime, $"{shortname}.{ext}");
        }

        // Entry-flavor: serialize the inline JSON payload from
        // entries.payload.body. The body is always JSON, so always inline —
        // no filename, no attachment header. UTF-8 explicit so non-ASCII
        // content (Arabic, Kurdish payloads) round-trips intact.
        var locator = new Locator(rt, space, normalizedSubpath, shortname);
        var entry = await entries.GetAsync(locator, actor, ct);
        if (entry?.Payload?.Body is null) return Results.NotFound();
        var bodyJson = JsonSerializer.Serialize(entry.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
        return Results.Content(bodyJson, MediaTypeNames.Application.Json, Encoding.UTF8);
    }

    internal static bool IsJsonResponse(string mime, string ext) =>
        string.Equals(mime, MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, "json", StringComparison.OrdinalIgnoreCase);

    public static string MimeFor(ContentType? contentType, string ext) => contentType switch
    {
        ContentType.Text       => "text/plain",
        ContentType.Markdown   => "text/markdown",
        ContentType.Html       => "text/html",
        ContentType.Json       => "application/json",
        ContentType.ImageJpeg  => "image/jpeg",
        ContentType.ImagePng   => "image/png",
        ContentType.ImageSvg   => "image/svg+xml",
        ContentType.ImageGif   => "image/gif",
        ContentType.ImageWebp  => "image/webp",
        ContentType.Pdf        => "application/pdf",
        ContentType.Audio      => "audio/mpeg",
        ContentType.Video      => "video/mp4",
        ContentType.Csv        => "text/csv",
        ContentType.Jsonl      => "application/jsonlines",
        ContentType.Python     => "text/x-python",
        ContentType.Apk        => "application/vnd.android.package-archive",
        ContentType.Sqlite     => "application/vnd.sqlite3",
        ContentType.Parquet    => "application/octet-stream",
        _                      => "application/octet-stream",
    };
}
