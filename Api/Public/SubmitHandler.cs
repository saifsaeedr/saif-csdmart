using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Public;

public static class SubmitHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Python routes use {subpath:path}; ASP.NET catch-all must sit at the
        // end, so parse the three supported forms from one endpoint:
        //   /submit/{space}/{schema}/{subpath:path}
        //   /submit/{space}/{resource_type}/{schema}/{subpath:path}
        //   /submit/{space}/{resource_type}/{workflow}/{schema}/{subpath:path}
        g.MapPost("/submit/{space}/{**rest}",
            async (string space, string rest, HttpRequest req,
                   EntryService entries, IOptions<DmartSettings> settings,
                   CancellationToken ct) =>
            {
                var parsed = ParseSubmitRest(rest);
                if (parsed.Error is not null)
                    return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                        parsed.Error, ErrorTypes.Request);
                return await SubmitAsync(space, parsed.ResourceType, parsed.Schema,
                    parsed.Subpath, parsed.Workflow, req, entries, settings, ct);
            })
            // Body is the schema payload (any JSON object — copied verbatim
            // into the created entry's payload.body). Documented as Record
            // for a familiar starting shape; the handler accepts any object.
            .Accepts<Record>("application/json")
            .Produces<Response>();
    }

    private static async Task<Response> SubmitAsync(
        string space, ResourceType rt, string schema, string subpath, string? workflow,
        HttpRequest req, EntryService entries, IOptions<DmartSettings> settings, CancellationToken ct)
    {
        if (rt is not (ResourceType.Content or ResourceType.Ticket))
            return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                "public submit only supports content and ticket", ErrorTypes.Request);

        if (rt == ResourceType.Ticket && string.IsNullOrWhiteSpace(workflow))
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                "Workflow shortname is required for ticket creation", ErrorTypes.Request);

        // Python parity: empty allowed_submit_models means public submit is
        // closed. Operators must explicitly list allowed "space.schema" pairs.
        var key = $"{space}.{schema}";
        if (!IsSubmitAllowed(settings.Value.AllowedSubmitModels, space, schema))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED_LOCATION,
                "Selected location is not allowed", ErrorTypes.Request);

        // Read the body as a raw JsonElement so we can carry it into Payload.Body losslessly.
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var body = doc.RootElement.Clone();
        Dictionary<string, object> rawAttrs;
        try
        {
            rawAttrs = JsonSerializer.Deserialize(
                body.GetRawText(), DmartJsonContext.Default.DictionaryStringObject)
                ?? new Dictionary<string, object>();
        }
        catch (JsonException)
        {
            rawAttrs = new Dictionary<string, object>();
        }
        // Anonymous callers never pick their own shortname — a "shortname"
        // field in the body is ignored for identity (it stays in Payload.Body
        // as data). Mint a fresh UUID-derived shortname server-side so one
        // caller can't squat on a name and concurrent submissions don't collide.
        var entryUuid = Guid.NewGuid();
        var shortname = entryUuid.ToString("N")[..16];

        var entry = new Entry
        {
            Uuid = entryUuid.ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = "/" + subpath.TrimStart('/'),
            ResourceType = rt,
            OwnerShortname = "anonymous",
            WorkflowShortname = workflow,
            State = workflow is null ? null : "submitted",
            IsOpen = workflow is null ? null : true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                SchemaShortname = schema,
                Body = body,
            },
        };
        // Public submit runs as the anonymous actor after the allowlist check.
        var result = await entries.CreateAsync(entry, actor: "anonymous", rawAttrs, ct: ct);
        if (!result.IsOk)
            return Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "request");

        var saved = result.Value!;
        var record = new Record
        {
            ResourceType = saved.ResourceType,
            Shortname = saved.Shortname,
            Subpath = saved.Subpath,
            Uuid = saved.Uuid,
            Attributes = new()
            {
                ["is_active"] = saved.IsActive,
                ["owner_shortname"] = saved.OwnerShortname,
                ["payload"] = saved.Payload!,
            },
        };
        if (!string.IsNullOrEmpty(saved.State)) record.Attributes["state"] = saved.State;
        if (!string.IsNullOrEmpty(saved.WorkflowShortname)) record.Attributes["workflow_shortname"] = saved.WorkflowShortname;
        if (saved.IsOpen.HasValue) record.Attributes["is_open"] = saved.IsOpen.Value;
        return Response.Ok(new[] { record },
            attributes: new() { ["uuid"] = saved.Uuid, ["shortname"] = saved.Shortname });
    }

    public static bool IsSubmitAllowed(string allowedSubmitModels, string space, string schema)
    {
        if (string.IsNullOrWhiteSpace(allowedSubmitModels)) return false;
        var key = $"{space}.{schema}";
        return allowedSubmitModels
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    private static (ResourceType ResourceType, string Schema, string Subpath, string? Workflow, string? Error) ParseSubmitRest(string rest)
    {
        var parts = (rest ?? "")
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return (ResourceType.Content, "", "", null, "invalid submit path");

        if (Enum.TryParse<PublicSubmitResourceType>(parts[0], ignoreCase: true, out var publicType))
        {
            var rt = publicType == PublicSubmitResourceType.Ticket ? ResourceType.Ticket : ResourceType.Content;
            if (rt == ResourceType.Ticket && parts.Length >= 4)
                return (rt, parts[2], string.Join('/', parts.Skip(3)), parts[1], null);
            if (parts.Length < 3)
                return (rt, "", "", null, "invalid submit path");
            return (rt, parts[1], string.Join('/', parts.Skip(2)), null, null);
        }

        return (ResourceType.Content, parts[0], string.Join('/', parts.Skip(1)), null, null);
    }
}
