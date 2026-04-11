using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Npgsql;

namespace Dmart.Plugins.BuiltIn;

// Port of dmart/backend/plugins/db_size_info/plugin.py. An API plugin that
// mounts GET /db_size_info/ and returns a per-table size list sourced from
// pg_total_relation_size for every public.* table, ordered largest first.
public sealed class DbSizeInfoPlugin(Db db) : IApiPlugin
{
    public string Shortname => "db_size_info";

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.MapGet("/", async Task<Response> (CancellationToken ct) =>
        {
            const string sql = """
                SELECT table_name,
                       pg_size_pretty(pg_total_relation_size(quote_ident(table_name))) AS pretty_size
                FROM information_schema.tables
                WHERE table_schema = 'public'
                ORDER BY pg_total_relation_size(quote_ident(table_name)) DESC
                """;

            try
            {
                await using var conn = await db.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(sql, conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var rows = new List<object>();
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        ["table_name"] = reader.GetString(0),
                        ["pretty_size"] = reader.GetString(1),
                    });
                }
                return Response.Ok(attributes: new()
                {
                    ["status"] = "success",
                    ["data"] = rows,
                });
            }
            catch (Exception ex)
            {
                return Response.Ok(attributes: new()
                {
                    ["status"] = "failed",
                    ["error"] = ex.Message,
                });
            }
        });
    }
}
