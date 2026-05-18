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
        })
        .Accepts<Query>("application/json")
        .Produces(200, contentType: "text/csv");

        // Saved query → CSV. The {space_name} param is a query named "saved-queries"
        // subpath in dmart convention; Python receives a Record pointing at the
        // saved query/report entry and then executes it.
        g.MapPost("/csv/{space_name}",
            async (string space_name, HttpRequest req, CsvService csv,
                   EntryService entries, QueryService queries, HttpContext http,
                   CancellationToken ct) =>
            {
                JsonDocument doc;
                try
                {
                    doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request));
                }
                using (doc)
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("resource_type", out _))
                    {
                        var record = doc.RootElement.Deserialize(DmartJsonContext.Default.Record);
                        if (record is null)
                            return Results.BadRequest(Response.Fail(InternalErrorCode.INVALID_DATA,
                                "record body is empty", ErrorTypes.Request));
                        var response = await ExecuteTaskHandler.ExecuteSavedQueryRecordAsync(
                            space_name, record, http.Actor(), entries, queries, ct);
                        if (response.Status != Status.Success)
                            return Results.BadRequest(response);
                        var recordStream = csv.ExportRecords(response.Records ?? Enumerable.Empty<Record>());
                        return Results.Stream(recordStream, "text/csv", $"{space_name}_{record.Subpath}.csv");
                    }

                    Query? q;
                    try
                    {
                        q = doc.RootElement.Deserialize(DmartJsonContext.Default.Query);
                    }
                    catch (JsonException ex)
                    {
                        return Results.BadRequest(Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request));
                    }
                    if (q is null) q = new Query { Type = QueryType.Search, SpaceName = space_name, Subpath = "/" };
                    var queryStream = await csv.ExportAsync(q, http.Actor(), ct);
                    return Results.Stream(queryStream, "text/csv", $"{space_name}.csv");
                }
            })
            // Body may be either a Query or a saved-query Record. Documenting
            // the Query form here is the common case; pasting a Record with
            // `resource_type` instead also works at runtime.
            .Accepts<Query>("application/json")
            .Produces(200, contentType: "text/csv");
    }
}
