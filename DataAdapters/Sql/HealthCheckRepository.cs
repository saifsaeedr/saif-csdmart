using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Runs the same integrity probes dmart Python's health check exposes:
//   * orphan attachments       — attachments with no matching parent entry
//   * dangling owners          — entries whose owner_shortname doesn't exist
//   * stale locks              — locks held longer than `staleAfter`
//   * empty payload references — entries that claim a payload but have no body
//   * untyped resources        — rows with unknown resource_type values
//
// `health_type` filters which checks run:
//   * "soft"      → counts only
//   * "hard"      → counts + sample rows
//   * "all"       → everything
public sealed class HealthCheckRepository(Db db)
{
    public sealed record IssueCheck(string Name, long Count, List<string> Samples);

    public async Task<List<IssueCheck>> RunAsync(string spaceName, string healthType, CancellationToken ct = default)
    {
        var includeSamples = healthType is "hard" or "all";
        var sampleLimit = includeSamples ? 10 : 0;
        var results = new List<IssueCheck>();

        await using var conn = await db.OpenAsync(ct);

        results.Add(await RunCheck(conn, "orphan_attachments", """
            SELECT a.shortname FROM attachments a
            WHERE a.space_name = $1
            AND NOT EXISTS (
                SELECT 1 FROM entries e
                WHERE e.space_name = a.space_name
                  AND a.subpath LIKE e.subpath || '/' || e.shortname || '%'
            )
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "dangling_owner", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND NOT EXISTS (SELECT 1 FROM users u WHERE u.shortname = e.owner_shortname)
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "stale_locks", """
            SELECT l.shortname FROM locks l
            WHERE l.space_name = $1 AND l.timestamp < (NOW() - INTERVAL '24 hours')
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "missing_payload_body", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND e.payload IS NOT NULL
            AND (e.payload->'body') IS NULL
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "missing_schema_reference", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND e.payload IS NOT NULL
            AND (e.payload->>'schema_shortname') IS NOT NULL
            AND NOT EXISTS (
                SELECT 1 FROM entries s
                WHERE s.space_name = e.space_name
                  AND s.resource_type = 'schema'
                  AND s.shortname = (e.payload->>'schema_shortname')
            )
            """, spaceName, sampleLimit, ct));

        // Folder content compliance: entries violating their PARENT folder's
        // declared policy arrays (content_resource_types /
        // content_schema_shortnames / workflow_shortnames). Mirrors
        // FolderContentValidator's rules — empty/absent array means
        // unrestricted, an absent incoming value skips that dimension — so the
        // report and the write-path gate agree on what "non-compliant" means.
        // This surfaces legacy rows that predate enforcement (and anything
        // written during an ENFORCE_FOLDER_CONTENT_POLICY=false dry-run).
        // The parent split duplicates FolderContentValidator.SplitSubpath in
        // SQL: '/a/b' -> folder 'b' under '/a'; '/a' -> folder 'a' under '/'.
        results.Add(await RunCheck(conn, "folder_content_violations", """
            SELECT e.subpath || '/' || e.shortname FROM entries e
            JOIN entries f
              ON f.space_name = e.space_name
             AND f.resource_type = 'folder'
             AND f.shortname = regexp_replace(e.subpath, '^.*/', '')
             AND f.subpath = COALESCE(NULLIF(left(e.subpath,
                   greatest(length(e.subpath) - length(regexp_replace(e.subpath, '^.*/', '')) - 1, 0)), ''), '/')
            WHERE e.space_name = $1
              AND e.subpath <> '/'
              AND jsonb_typeof(f.payload->'body') = 'object'
              AND (
                (jsonb_typeof(f.payload->'body'->'content_resource_types') = 'array'
                 AND jsonb_array_length(f.payload->'body'->'content_resource_types') > 0
                 AND NOT (f.payload->'body'->'content_resource_types' @> to_jsonb(e.resource_type::text)))
                OR
                ((e.payload->>'schema_shortname') IS NOT NULL
                 AND jsonb_typeof(f.payload->'body'->'content_schema_shortnames') = 'array'
                 AND jsonb_array_length(f.payload->'body'->'content_schema_shortnames') > 0
                 AND NOT (f.payload->'body'->'content_schema_shortnames' @> to_jsonb(e.payload->>'schema_shortname')))
                OR
                (e.resource_type = 'ticket'
                 AND e.workflow_shortname IS NOT NULL
                 AND jsonb_typeof(f.payload->'body'->'workflow_shortnames') = 'array'
                 AND jsonb_array_length(f.payload->'body'->'workflow_shortnames') > 0
                 AND NOT (f.payload->'body'->'workflow_shortnames' @> to_jsonb(e.workflow_shortname)))
              )
            """, spaceName, sampleLimit, ct));

        return results;
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `sql` is a compile-time constant from in-class call sites (the five HealthCheck SQL literals); `spaceName` flows through $1.")]
    private static async Task<IssueCheck> RunCheck(
        NpgsqlConnection conn, string name, string sql, string spaceName,
        int sampleLimit, CancellationToken ct)
    {
        var samples = new List<string>();
        long count = 0;

        // Count
        await using (var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM ({sql}) c", conn))
        {
            cmd.Parameters.Add(new() { Value = spaceName });
            count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        // Samples
        if (sampleLimit > 0 && count > 0)
        {
            await using var cmd = new NpgsqlCommand($"{sql} LIMIT {sampleLimit}", conn);
            cmd.Parameters.Add(new() { Value = spaceName });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) samples.Add(reader.GetString(0));
        }

        return new IssueCheck(name, count, samples);
    }
}
