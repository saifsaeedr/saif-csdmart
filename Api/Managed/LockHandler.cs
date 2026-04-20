using Dmart.Api;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class LockHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // PUT /lock/{resource_type}/{space}/{subpath:path}/{shortname}
        g.MapPut("/lock/{resource_type}/{space}/{**rest}",
            async Task<Response> (string resource_type, string space, string rest,
                   LockService svc, HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "unknown resource type", ErrorTypes.Request);
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname))
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "shortname required", ErrorTypes.Request);
                return await svc.LockAsync(new Locator(rt, space, subpath, shortname),
                    http.Actor(), ct);
            });

        // DELETE /lock/{space}/{subpath:path}/{shortname}
        g.MapDelete("/lock/{space}/{**rest}",
            async Task<Response> (string space, string rest,
                   LockService svc, HttpContext http, CancellationToken ct) =>
            {
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname))
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "shortname required", ErrorTypes.Request);
                return await svc.UnlockAsync(
                    new Locator(ResourceType.Content, space, subpath, shortname),
                    http.Actor(), ct);
            });
    }
}
