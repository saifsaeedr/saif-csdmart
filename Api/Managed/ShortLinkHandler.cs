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
        // Python parity: api/managed/router.py::shoting_url has no JWTBearer
        // dependency — short-link recipients follow this link before
        // they have a session, so the /managed group's RequireAuthorization()
        // must be opted out of here. The auth-by-ip limiter (Program.cs:1126)
        // is reused: this endpoint sits on the same anti-abuse surface as the
        // auth endpoints (anonymous), so a
        // separate budget would just duplicate the policy.
        g.MapGet("/s/{token}", async (string token, ShortLinkService svc, CancellationToken ct) =>
        {
            var url = await svc.ResolveAsync(token, ct);
            return url is null ? Results.NotFound() : Results.Redirect(url);
        }).AllowAnonymous().RequireRateLimiting("auth-by-ip");

        // Creator: GET /managed/shortening/{space}/{**rest} → creates a short URL.
        // Python: GET /managed/shortening/{space}/{subpath}/{shortname}
        g.MapGet("/shortening/{space}/{**rest}", async (
            string space, string rest,
            ShortLinkService svc, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            var parts = rest.Split('/');
            if (parts.Length < 1)
                return Response.Fail(InternalErrorCode.MISSING_DATA, "shortname required", ErrorTypes.Request);
            var shortname = parts[^1];
            var subpath = parts.Length > 1 ? string.Join("/", parts[..^1]) : "/";
            // 128-bit CSPRNG token (32 hex) — the old 8-hex (32-bit) token was
            // brute-force enumerable by the anonymous resolver.
            var token = System.Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
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
