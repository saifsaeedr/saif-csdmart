using Dmart.Api;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using System.Text.Json;

namespace Dmart.Api.Managed;

public static class ProgressTicketHandler
{
    public static void Map(RouteGroupBuilder g) =>
        // PUT /progress-ticket/{space}/{subpath:path}/{shortname}/{action}
        g.MapPut("/progress-ticket/{space}/{**rest}",
            async (string space, string rest, HttpRequest req,
                   WorkflowService wf, HttpContext http, CancellationToken ct) =>
            {
                var parts = RouteParts.SplitProgressTicketParts(rest);
                if (parts is null)
                    return Response.Fail(InternalErrorCode.MISSING_DATA,
                        "expected /progress-ticket/{space}/{subpath}/{shortname}/{action}", ErrorTypes.Request);
                var (subpath, shortname, action) = parts.Value;
                var attrs = await ReadBodyAttrsAsync(req, ct);
                return await wf.ProgressAsync(
                    new Locator(ResourceType.Ticket, space, subpath, shortname),
                    action, http.Actor(), attrs, ct);
            })
            .Accepts<Dmart.Models.Api.ProgressTicketBody>("application/json")
            .Produces<Response>();

    private static async Task<Dictionary<string, object>?> ReadBodyAttrsAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is 0 or null) return null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var attrs = new Dictionary<string, object>();
            if (doc.RootElement.TryGetProperty("resolution", out var resolution))
                attrs["resolution_reason"] = resolution.Clone();
            if (doc.RootElement.TryGetProperty("resolution_reason", out var resolutionReason))
                attrs["resolution_reason"] = resolutionReason.Clone();
            if (doc.RootElement.TryGetProperty("comment", out var comment))
                attrs["comment"] = comment.Clone();
            return attrs;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
