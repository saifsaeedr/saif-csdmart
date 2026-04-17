using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Managed;

public static class ShortLinkHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Resolver: GET /managed/s/{token} → 302 redirect.
        g.MapGet("/s/{token}", async (string token, ShortLinkService svc, CancellationToken ct) =>
        {
            var url = await svc.ResolveAsync(token, ct);
            return url is null ? Results.NotFound() : Results.Redirect(url);
        });

        // Creator: GET /managed/shortening/{space}/{**rest} → creates a short URL.
        // Python: GET /managed/shortening/{space}/{subpath}/{shortname}
        g.MapGet("/shortening/{space}/{**rest}", async (
            string space, string rest,
            ShortLinkService svc, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            var parts = rest.Split('/');
            if (parts.Length < 1)
                return Response.Fail(InternalErrorCode.MISSING_DATA, "shortname required", "request");
            var shortname = parts[^1];
            var subpath = parts.Length > 1 ? string.Join("/", parts[..^1]) : "/";
            var token = Guid.NewGuid().ToString("N")[..8];
            var appUrl = settings.Value.AppUrl;
            var fullUrl = $"{appUrl}/managed/entry/content/{space}/{subpath}/{shortname}";
            var expires = settings.Value.UrlShorterExpires;
            await svc.CreateAsync(token, fullUrl, TimeSpan.FromSeconds(expires), ct);
            return Response.Ok(attributes: new()
            {
                ["token"] = token,
                ["short_url"] = $"{appUrl}/managed/s/{token}",
            });
        });
    }
}
