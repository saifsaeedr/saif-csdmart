using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Dmart.Api.Mcp;

// Maps dmart:// URIs to actual data reads. Supported shapes:
//
//   dmart://spaces
//     → JSON array of the spaces the caller has any access to.
//
//   dmart://<space>
//     → alias for a query against the space's root; returns root-level
//       records the caller can see.
//
//   dmart://<space>/<subpath>
//     → collection listing under that subpath (limit 50).
//
//   dmart://<space>/<subpath>/<shortname>
//     → a single entry. `resource_type=content` by default; the caller can
//       override with a query string (?type=ticket, ?type=folder, ...).
//
// All reads go through QueryService.ExecuteAsync with the caller's actor,
// so permissions are enforced identically to the HTTP API.
public static class McpResourceResolver
{
    public static async Task<string> ReadAsync(string uri, HttpContext http, CancellationToken ct)
    {
        if (!uri.StartsWith("dmart://", StringComparison.Ordinal))
            throw new ArgumentException($"unsupported URI scheme: {uri}");

        var actor = http.User.Identity?.Name
            ?? throw new UnauthorizedAccessException("login required");
        var qs = http.RequestServices.GetRequiredService<QueryService>();

        var path = uri.Substring("dmart://".Length);

        if (path == "spaces")
        {
            var q = new Query
            {
                Type = QueryType.Spaces,
                SpaceName = "management",
                Subpath = "/",
                Limit = 50,
            };
            var resp = await qs.ExecuteAsync(q, actor, ct);
            return SerializeResponse(resp);
        }

        // Split into (space, remainder). Remainder may itself contain subpath
        // segments plus an optional final shortname.
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException($"malformed dmart URI: {uri}");

        var space = parts[0];
        if (parts.Length == 1)
        {
            // dmart://<space> — list root entries of the space.
            var q = new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = "/",
                Limit = 50,
            };
            var resp = await qs.ExecuteAsync(q, actor, ct);
            return SerializeResponse(resp);
        }

        // dmart://<space>/<segment1>/.../<segmentN>
        // Convention: treat the LAST segment as a shortname, everything
        // between as the subpath. For a pure-collection URI, callers add a
        // trailing slash — which `Split(RemoveEmptyEntries)` strips — so we
        // check the raw path to distinguish.
        var isCollection = path.EndsWith('/');
        if (isCollection)
        {
            var subpath = "/" + string.Join("/", parts.Skip(1));
            var q = new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = subpath,
                Limit = 50,
            };
            var resp = await qs.ExecuteAsync(q, actor, ct);
            return SerializeResponse(resp);
        }

        // Single entry.
        var shortname = parts[^1];
        var subpathSegments = parts.Skip(1).Take(parts.Length - 2).ToList();
        var entrySubpath = subpathSegments.Count == 0 ? "/" : "/" + string.Join("/", subpathSegments);
        var entryQuery = new Query
        {
            Type = QueryType.Search,
            SpaceName = space,
            Subpath = entrySubpath,
            FilterShortnames = [shortname],
            RetrieveJsonPayload = true,
            Limit = 1,
        };
        var entryResp = await qs.ExecuteAsync(entryQuery, actor, ct);
        return SerializeResponse(entryResp);
    }

    private static string SerializeResponse(Response resp)
        => JsonSerializer.Serialize(resp, DmartJsonContext.Default.Response);
}
