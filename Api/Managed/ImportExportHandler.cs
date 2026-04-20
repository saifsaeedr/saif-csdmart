using Dmart.Models.Api;
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

        g.MapPost("/export",
            async (string? space, string? subpath,
                   ImportExportService io, HttpContext http, CancellationToken ct) =>
            {
                if (string.IsNullOrEmpty(space))
                    return Results.BadRequest(Response.Fail(InternalErrorCode.INVALID_SPACE_NAME, "space required", ErrorTypes.Request));
                var stream = await io.ExportAsync(space, subpath, http.Actor(), ct);
                return Results.Stream(stream, "application/zip", $"{space}.zip");
            });
    }
}
