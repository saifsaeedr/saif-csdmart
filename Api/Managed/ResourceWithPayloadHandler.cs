using System.Security.Cryptography;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// Mirrors dmart's POST /managed/resource_with_payload (api/managed/router.py +
// api/managed/utils.py::create_or_update_resource_with_payload_handler).
//
// Multipart fields (matching dmart):
//   * payload_file   (file)        — the binary or JSON content
//   * request_record (file)        — JSON-encoded core.Record
//   * space_name     (form text)
//   * sha            (form text)   — optional client-side checksum
//
// Routing rule (matching dmart):
//   * Attachment-flavor resource types (comment, media, json, reaction, reply, share,
//     relationship, alteration, lock, data_asset)         → attachments table; bytes
//                                                          stored in attachments.media
//                                                          column; payload.body holds
//                                                          the filename string
//   * Content / Ticket / Schema                          → entries table; payload file
//                                                          must be JSON, parsed and
//                                                          stored in entries.payload.body
public static class ResourceWithPayloadHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/resource_with_payload",
            async Task<Response> (HttpRequest req, EntryService entries,
                                  AttachmentRepository attachments, HttpContext http,
                                  CancellationToken ct) =>
                await HandleAsync(req, entries, attachments, http.User.Identity?.Name ?? "anonymous", ct))
          .DisableAntiforgery();

        g.MapPost("/resources_from_csv/{resource_type}/{space}/{subpath}/{schema}",
            async Task<Response> (string resource_type, string space, string subpath, string schema,
                                  HttpRequest req, CsvService csv, HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "unknown resource type", "request");
                if (!req.HasFormContentType)
                    return Response.Fail(InternalErrorCode.INVALID_DATA, "expected multipart/form-data", "request");

                var form = await req.ReadFormAsync(ct);
                var csvFile = form.Files["resources_file"] ?? form.Files.FirstOrDefault();
                if (csvFile is null)
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "csv file required", "request");

                await using var stream = csvFile.OpenReadStream();
                return await csv.ImportAsync(space, "/" + subpath.TrimStart('/'), rt,
                    string.IsNullOrEmpty(schema) ? null : schema,
                    stream, http.User.Identity?.Name, ct);
            })
          .DisableAntiforgery();
    }

    public static async Task<Response> HandleAsync(
        HttpRequest req, EntryService entries, AttachmentRepository attachments,
        string actor, CancellationToken ct)
    {
        if (!req.HasFormContentType)
            return Response.Fail(InternalErrorCode.INVALID_DATA, "expected multipart/form-data", "request");

        var form = await req.ReadFormAsync(ct);
        var spaceName = form["space_name"].ToString();
        var sha = form["sha"].FirstOrDefault();
        var payloadFile = form.Files["payload_file"];
        var requestRecordFile = form.Files["request_record"];

        if (string.IsNullOrEmpty(spaceName))
            return Response.Fail(InternalErrorCode.INVALID_SPACE_NAME, "space_name is required", "request");
        if (payloadFile is null)
            return Response.Fail(InternalErrorCode.MISSING_DATA, "payload_file is required", "request");
        if (requestRecordFile is null)
            return Response.Fail(InternalErrorCode.MISSING_DATA, "request_record is required", "request");

        Record? record;
        try
        {
            await using var recStream = requestRecordFile.OpenReadStream();
            record = await JsonSerializer.DeserializeAsync(recStream, DmartJsonContext.Default.Record, ct);
        }
        catch (JsonException)
        {
            return Response.Fail(InternalErrorCode.INVALID_DATA, "invalid request body", "request");
        }
        if (record is null)
            return Response.Fail(InternalErrorCode.INVALID_DATA, "request_record is empty", "request");

        // Python: shortname "auto" → generate from UUID first 8 chars.
        record = RequestHandler.ResolveAutoShortname(record);

        // Read full bytes (acceptable here — dmart also reads the whole file at once
        // because it computes a sha256 over the entire payload before storing).
        byte[] fileBytes;
        await using (var src = payloadFile.OpenReadStream())
        await using (var ms = new MemoryStream())
        {
            await src.CopyToAsync(ms, ct);
            fileBytes = ms.ToArray();
        }

        const int MaxPayloadSize = 50 * 1024 * 1024; // 50 MB
        if (fileBytes.Length > MaxPayloadSize)
            return Response.Fail(InternalErrorCode.INVALID_DATA, $"payload file exceeds {MaxPayloadSize / (1024 * 1024)}MB limit", "request");

        var checksum = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        if (!string.IsNullOrEmpty(sha) && !string.Equals(sha, checksum, StringComparison.OrdinalIgnoreCase))
            return Response.Fail(InternalErrorCode.INVALID_DATA, "the provided file doesn't match the sha", "request");

        var ext = (Path.GetExtension(payloadFile.FileName) ?? "").TrimStart('.').ToLowerInvariant();
        var resourceContentType = InferContentType(payloadFile.ContentType, ext);
        var schemaShortname = ExtractSchemaShortname(record.Attributes);

        if (IsAttachmentResourceType(record.ResourceType))
            return await StoreAttachmentAsync(record, spaceName, actor, fileBytes, ext,
                resourceContentType, checksum, sha, schemaShortname, attachments, ct);

        return await StoreEntryAsync(record, spaceName, actor, fileBytes, ext,
            resourceContentType, checksum, sha, schemaShortname, entries, ct);
    }

    private static async Task<Response> StoreAttachmentAsync(
        Record record, string spaceName, string actor, byte[] fileBytes, string ext,
        ContentType contentType, string checksum, string? clientChecksum, string? schemaShortname,
        AttachmentRepository attachments, CancellationToken ct)
    {
        var bodyRef = $"{record.Shortname}.{ext}";
        var attachment = new Attachment
        {
            Uuid = string.IsNullOrEmpty(record.Uuid) ? Guid.NewGuid().ToString() : record.Uuid,
            Shortname = record.Shortname,
            SpaceName = spaceName,
            Subpath = record.Subpath,
            ResourceType = record.ResourceType,
            OwnerShortname = actor,
            IsActive = true,
            Media = fileBytes,
            // dmart sets payload.body to the filename string for attachment-typed
            // resources; the actual bytes go into the media column.
            Payload = new Payload
            {
                ContentType = contentType,
                Checksum = checksum,
                ClientChecksum = clientChecksum,
                SchemaShortname = schemaShortname,
                Body = StringJsonElement(bodyRef),
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        try
        {
            await attachments.UpsertAsync(attachment, ct);
        }
        catch (Exception)
        {
            return Response.Fail(InternalErrorCode.OBJECT_NOT_SAVED,
                "failed to save attachment", "attachment");
        }

        return Response.Ok(records: new[] { record with { Uuid = attachment.Uuid } });
    }

    private static async Task<Response> StoreEntryAsync(
        Record record, string spaceName, string actor, byte[] fileBytes, string ext,
        ContentType contentType, string checksum, string? clientChecksum, string? schemaShortname,
        EntryService entries, CancellationToken ct)
    {
        // dmart only allows Content/Ticket/Schema here, all of which expect JSON payloads.
        // Parse the file as JSON and inline it into payload.body.
        JsonElement? body;
        try
        {
            using var doc = JsonDocument.Parse(fileBytes);
            body = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                "invalid request body", "request");
        }

        var entry = new Entry
        {
            Uuid = string.IsNullOrEmpty(record.Uuid) ? Guid.NewGuid().ToString() : record.Uuid,
            Shortname = record.Shortname,
            SpaceName = spaceName,
            Subpath = record.Subpath,
            ResourceType = record.ResourceType,
            OwnerShortname = actor,
            IsActive = true,
            Payload = new Payload
            {
                ContentType = contentType,
                Checksum = checksum,
                ClientChecksum = clientChecksum,
                // dmart's schema rules: meta_schema for schema resources, otherwise
                // pick from the record's attributes.payload.schema_shortname.
                SchemaShortname = record.ResourceType == ResourceType.Schema
                    ? "meta_schema" : schemaShortname,
                Body = body,
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await entries.CreateAsync(entry, actor, ct);
        if (!result.IsOk)
            return Response.Fail(result.ErrorCode!, result.ErrorMessage!);
        return Response.Ok(records: new[] { record with { Uuid = result.Value!.Uuid } });
    }

    // AOT-safe JsonElement of kind String — built without going through reflection
    // serialization (which trips IL2026/IL3050).
    private static JsonElement StringJsonElement(string value)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
            writer.WriteStringValue(value);
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    // dmart's set of attachment-flavor resource types (anything stored in the
    // attachments table). All other types live in the entries table.
    public static bool IsAttachmentResourceType(ResourceType type) => type switch
    {
        ResourceType.Comment      => true,
        ResourceType.Reply        => true,
        ResourceType.Reaction     => true,
        ResourceType.Media        => true,
        ResourceType.Json         => true,
        ResourceType.Share        => true,
        ResourceType.Relationship => true,
        ResourceType.Alteration   => true,
        ResourceType.Lock         => true,
        ResourceType.DataAsset    => true,
        _                         => false,
    };

    // Pulls schema_shortname out of attributes.payload.schema_shortname (the dmart
    // wire convention). Tolerant of either a JsonElement or a Dictionary value.
    private static string? ExtractSchemaShortname(Dictionary<string, object>? attrs)
    {
        if (attrs is null || !attrs.TryGetValue("payload", out var payloadObj)) return null;
        if (payloadObj is JsonElement el && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("schema_shortname", out var ss) && ss.ValueKind == JsonValueKind.String)
            return ss.GetString();
        if (payloadObj is Dictionary<string, object> d
            && d.TryGetValue("schema_shortname", out var ssRaw))
            return ssRaw?.ToString();
        return null;
    }

    // Maps an HTTP MIME type or filename extension to dmart's ContentType enum.
    public static ContentType InferContentType(string? mime, string ext)
    {
        var mt = (mime ?? "").ToLowerInvariant();
        return mt switch
        {
            "image" => ContentType.Image,
            "image/jpeg" => ContentType.ImageJpeg,
            "image/png"  => ContentType.ImagePng,
            "image/svg+xml" => ContentType.ImageSvg,
            "image/gif"  => ContentType.ImageGif,
            "image/webp" => ContentType.ImageWebp,
            "application/pdf" => ContentType.Pdf,
            "audio/mpeg" => ContentType.Audio,
            "video/mp4"  => ContentType.Video,
            "text/plain" => ContentType.Text,
            "text/markdown" => ContentType.Markdown,
            "text/html"  => ContentType.Html,
            "text/csv"   => ContentType.Csv,
            "application/json" => ContentType.Json,
            "application/jsonlines" or "application/x-ndjson" => ContentType.Jsonl,
            "application/x-python" or "text/x-python" => ContentType.Python,
            "application/vnd.android.package-archive" => ContentType.Apk,
            "application/vnd.sqlite3" => ContentType.Sqlite,
            _ => ext switch
            {
                "jpg" or "jpeg" => ContentType.ImageJpeg,
                "png"  => ContentType.ImagePng,
                "svg"  => ContentType.ImageSvg,
                "gif"  => ContentType.ImageGif,
                "webp" => ContentType.ImageWebp,
                "pdf"  => ContentType.Pdf,
                "mp3" or "wav" or "ogg" => ContentType.Audio,
                "mp4" or "mov" or "webm" => ContentType.Video,
                "txt"  => ContentType.Text,
                "md"   => ContentType.Markdown,
                "html" or "htm" => ContentType.Html,
                "csv"  => ContentType.Csv,
                "json" => ContentType.Json,
                "jsonl" or "ndjson" => ContentType.Jsonl,
                "py"   => ContentType.Python,
                "apk"  => ContentType.Apk,
                "duckdb" => ContentType.Duckdb,
                "sqlite" or "db" => ContentType.Sqlite,
                "parquet" => ContentType.Parquet,
                _ => ContentType.Json,
            },
        };
    }
}
