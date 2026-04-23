using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class ImportExportHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Import: multipart with a `zip_file` part. dmart's signature uses `extra` as
        // an optional path prefix; we accept it but currently ignore it (not used by
        // most clients).
        g.MapPost("/import",
            async (HttpRequest req, ImportExportService io, HttpContext http, CancellationToken ct) =>
            {
                Stream zipStream;
                if (req.HasFormContentType)
                {
                    var form = await req.ReadFormAsync(ct);
                    var zipFile = form.Files["zip_file"];
                    if (zipFile is null)
                        return Response.Fail(InternalErrorCode.MISSING_DATA, "zip_file required", ErrorTypes.Request);
                    zipStream = zipFile.OpenReadStream();
                }
                else
                {
                    // dmart Python also accepts a raw application/zip body
                    zipStream = req.Body;
                }
                return await io.ImportZipAsync(zipStream, http.Actor(), ct);
            }).DisableAntiforgery();

        // Python-parity: POST /export takes a Query JSON body (not query-string
        // args). Mirrors dmart_plain/backend/api/managed/router.py::export_data
        // which signature is `export_data(query: api.Query, ...)`. The Query
        // fields (subpath, filter_types, filter_shortnames, search, offset,
        // limit) all feed the selection. Previously we took `?space=&subpath=`
        // on the query string — clients sending a Query body always got
        // "space required" because `space` was unbound.
        g.MapPost("/export",
            async (HttpRequest req, ImportExportService io, HttpContext http, CancellationToken ct) =>
            {
                Query? query;
                try
                {
                    query = await JsonSerializer.DeserializeAsync(
                        req.Body, DmartJsonContext.Default.Query, ct);
                }
                catch (JsonException ex)
                {
                    return Results.Json(
                        Response.Fail(InternalErrorCode.INVALID_DATA,
                            $"invalid export query body: {ex.Message}", ErrorTypes.Request),
                        DmartJsonContext.Default.Response, statusCode: 400);
                }
                if (query is null || string.IsNullOrEmpty(query.SpaceName))
                    return Results.Json(
                        Response.Fail(InternalErrorCode.INVALID_SPACE_NAME,
                            "space_name is required in the export query body", ErrorTypes.Request),
                        DmartJsonContext.Default.Response, statusCode: 400);

                var stream = await io.ExportAsync(query, http.Actor(), ct);
                return Results.Stream(stream, "application/zip", $"{query.SpaceName}.zip");
            });
    }
}
