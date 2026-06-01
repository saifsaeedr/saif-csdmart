using Dmart.Api.Managed;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;

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
                                  PermissionService perms,
                                  ILogger<ResourceWithPayloadMarker> log, CancellationToken ct) =>
                await ResourceWithPayloadHandler.HandleAsync(req, entries, attachments,
                    perms, "anonymous", log, ct))
          .Produces<Response>()
          // Anonymous upload endpoint — throttle per IP against attachment floods.
          .RequireRateLimiting("auth-by-ip")
          .DisableAntiforgery();

        g.MapPost("/attach/{space_name}",
            async Task<Response> (string space_name, HttpRequest req, EntryService entries,
                                  AttachmentRepository attachments, PermissionService perms,
                                  IOptions<DmartSettings> settings,
                                  ILogger<ResourceWithPayloadMarker> log, CancellationToken ct) =>
                await HandleAttachAsync(space_name, req, entries, attachments, perms, settings.Value, log, ct))
          .Produces<Response>()
          // Anonymous upload endpoint — throttle per IP against attachment floods.
          .RequireRateLimiting("auth-by-ip")
          .DisableAntiforgery();
    }

    private static async Task<Response> HandleAttachAsync(
        string spaceName, HttpRequest req, EntryService entries, AttachmentRepository attachments,
        PermissionService perms, DmartSettings settings, ILogger log, CancellationToken ct)
    {
        if (!req.HasFormContentType)
            return Response.Fail(InternalErrorCode.INVALID_DATA, "expected multipart/form-data", ErrorTypes.Request);

        var form = await req.ReadFormAsync(ct);

        // Back-compat: the C# public endpoint originally accepted the same
        // multipart shape as /resource_with_payload. Keep that path working
        // while also supporting Python's public /attach contract below.
        if (form.Files["request_record"] is not null)
            return await ResourceWithPayloadHandler.HandleAsync(req, entries, attachments,
                perms, "anonymous", log, ct);

        if (!SpaceAllowed(settings.AllowedSubmitModels, spaceName))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED_LOCATION,
                "Selected location is not allowed", ErrorTypes.Request);

        var recordRaw = form["record"].ToString();
        if (string.IsNullOrWhiteSpace(recordRaw))
            return Response.Fail(InternalErrorCode.MISSING_DATA, "record is required", ErrorTypes.Request);

        Record? record;
        try
        {
            record = JsonSerializer.Deserialize(recordRaw, DmartJsonContext.Default.Record);
        }
        catch (JsonException ex)
        {
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                $"invalid record JSON: {ex.Message}", ErrorTypes.Request);
        }
        if (record is null)
            return Response.Fail(InternalErrorCode.INVALID_DATA, "record is empty", ErrorTypes.Request);
        if (!ResourceWithPayloadHandler.IsAttachmentResourceType(record.ResourceType))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                "Only attachment resource types are allowed", ErrorTypes.Request);

        record = record with { Shortname = "auto" };
        record = RequestHandler.ResolveAutoShortname(record);

        var payloadFile = form.Files["payload_file"];
        if (payloadFile is not null)
        {
            await using var stream = payloadFile.OpenReadStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var ext = (Path.GetExtension(payloadFile.FileName) ?? "").TrimStart('.').ToLowerInvariant();
            var contentType = ResourceWithPayloadHandler.InferContentType(payloadFile.ContentType, ext);
            return await ResourceWithPayloadHandler.StoreAttachmentAsync(
                record, spaceName, "anonymous", bytes, ext, contentType, checksum, null,
                ResourceWithPayloadHandler.ExtractSchemaShortname(record.Attributes),
                attachments, perms, log, ct);
        }

        var attrs = record.Attributes ?? new();
        var gate = new Locator(record.ResourceType, spaceName, "/" + record.Subpath.TrimStart('/'), record.Shortname);
        if (!await perms.CanCreateAsync("anonymous", gate, attrs, ct))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                "You don't have permission to this action", ErrorTypes.Request);

        var attachment = new Attachment
        {
            Uuid = string.IsNullOrEmpty(record.Uuid) ? Guid.NewGuid().ToString() : record.Uuid,
            Shortname = record.Shortname,
            SpaceName = spaceName,
            Subpath = "/" + record.Subpath.TrimStart('/'),
            ResourceType = record.ResourceType,
            OwnerShortname = "anonymous",
            IsActive = !attrs.TryGetValue("is_active", out var isActive) || IsTruthy(isActive),
            Body = attrs.TryGetValue("body", out var body) ? ScalarToString(body) : null,
            State = attrs.TryGetValue("state", out var state) ? ScalarToString(state) : null,
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        };
        await attachments.UpsertAsync(attachment, ct);
        return Response.Ok(new[] { record with { Uuid = attachment.Uuid } });
    }

    private static bool SpaceAllowed(string allowedSubmitModels, string spaceName)
    {
        if (string.IsNullOrWhiteSpace(allowedSubmitModels)) return false;
        return allowedSubmitModels
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(pair => pair.StartsWith(spaceName + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTruthy(object value) => value switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement el when el.ValueKind == JsonValueKind.String =>
            bool.TryParse(el.GetString(), out var b) && b,
        string s => bool.TryParse(s, out var b) && b,
        _ => false,
    };

    private static string? ScalarToString(object value) =>
        value is JsonElement el
            ? el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText()
            : value.ToString();
}
