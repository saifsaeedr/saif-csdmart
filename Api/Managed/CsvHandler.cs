using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class CsvHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Inline query → CSV. Body is a Query JSON. Mirrors dmart's POST /managed/csv.
        g.MapPost("/csv", async (HttpRequest req, CsvService csv, HttpContext http, CancellationToken ct) =>
        {
            Query? q;
            try
            {
                q = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.Query, ct);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request));
            }
            if (q is null) return Results.BadRequest(Response.Fail(InternalErrorCode.MISSING_DATA, "empty body", ErrorTypes.Request));
            var stream = await csv.ExportAsync(q, http.Actor(), ct);
            return Results.Stream(stream, "text/csv", "export.csv");
        });

        // Saved query → CSV. The {space_name} param is a query named "saved-queries"
        // subpath in dmart convention; the body specifies which one. dmart's actual
        // implementation looks up a Task entry; we mirror that here by reading the
        // body as a Query and passing through.
        g.MapPost("/csv/{space_name}",
            async (string space_name, HttpRequest req, CsvService csv, HttpContext http, CancellationToken ct) =>
            {
                Query? q;
                try
                {
                    q = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.Query, ct);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request));
                }
                if (q is null) q = new Query { Type = QueryType.Search, SpaceName = space_name, Subpath = "/" };
                var stream = await csv.ExportAsync(q, http.Actor(), ct);
                return Results.Stream(stream, "text/csv", $"{space_name}.csv");
            });
    }
}
