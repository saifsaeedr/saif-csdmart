using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class HealthHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/health/{health_type}/{space_name}",
            async (string health_type, string space_name, HealthCheckRepository repo, CancellationToken ct) =>
            {
                var checks = await repo.RunAsync(space_name, health_type, ct);
                // Cast counts to int — source-gen JSON's Dictionary<string,object> path
                // doesn't know how to box arbitrary `long` values without reflection.
                var totalIssues = (int)checks.Sum(c => c.Count);
                var attributes = new Dictionary<string, object>
                {
                    ["space_name"] = space_name,
                    ["health_type"] = health_type,
                    ["total_issues"] = totalIssues,
                    ["checks"] = checks.Select(c => new Dictionary<string, object>
                    {
                        ["name"] = c.Name,
                        ["count"] = (int)c.Count,
                        ["samples"] = c.Samples,
                    }).ToList(),
                };
                return Response.Ok(attributes: attributes);
            });

        g.MapGet("/reload-security-data",
            async (PermissionService perms, SchemaValidator schemas, CancellationToken ct) =>
            {
                await perms.ReloadAsync(ct);
                // Also drop any compiled schemas — a permission refresh is a
                // reasonable operator-level signal to rebuild in-memory state.
                schemas.ClearCache();
                return Response.Ok();
            });
    }
}
