using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// Maps the `entries` table column-for-column to the C# Entry record. No `doc` jsonb
// fast-path; every column is read and written explicitly so dmart Python and dmart C#
// see the same row layout.
public sealed class EntryRepository(Db db)
{
    private const string SelectAllColumns = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, acl, payload, relationships,
               last_checksum_history, resource_type,
               state, is_open, reporter, workflow_shortname, collaborators,
               resolution_reason, query_policies
        FROM entries
        """;

    public async Task<Entry?> GetAsync(string spaceName, string subpath, string shortname, ResourceType type, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"{SelectAllColumns} WHERE space_name = $1 AND subpath = $2 AND shortname = $3 AND resource_type = $4",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(type) });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    public async Task<Entry?> GetByUuidAsync(Guid uuid, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectAllColumns} WHERE uuid = $1", conn);
        cmd.Parameters.Add(new() { Value = uuid });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    public async Task<Entry?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectAllColumns} WHERE slug = $1", conn);
        cmd.Parameters.Add(new() { Value = slug });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    public async Task UpsertAsync(Entry e, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO entries (uuid, shortname, space_name, subpath, is_active, slug,
                                 displayname, description, tags, created_at, updated_at,
                                 owner_shortname, owner_group_shortname, acl, payload, relationships,
                                 last_checksum_history, resource_type,
                                 state, is_open, reporter, workflow_shortname, collaborators,
                                 resolution_reason, query_policies)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25)
            ON CONFLICT (shortname, space_name, subpath) DO UPDATE SET
                is_active = EXCLUDED.is_active,
                slug = EXCLUDED.slug,
                displayname = EXCLUDED.displayname,
                description = EXCLUDED.description,
                tags = EXCLUDED.tags,
                updated_at = NOW(),
                owner_group_shortname = EXCLUDED.owner_group_shortname,
                acl = EXCLUDED.acl,
                payload = EXCLUDED.payload,
                relationships = EXCLUDED.relationships,
                last_checksum_history = EXCLUDED.last_checksum_history,
                resource_type = EXCLUDED.resource_type,
                state = EXCLUDED.state,
                is_open = EXCLUDED.is_open,
                reporter = EXCLUDED.reporter,
                workflow_shortname = EXCLUDED.workflow_shortname,
                collaborators = EXCLUDED.collaborators,
                resolution_reason = EXCLUDED.resolution_reason,
                query_policies = EXCLUDED.query_policies
            """, conn);

        cmd.Parameters.Add(new() { Value = Guid.Parse(e.Uuid) });
        cmd.Parameters.Add(new() { Value = e.Shortname });
        cmd.Parameters.Add(new() { Value = e.SpaceName });
        cmd.Parameters.Add(new() { Value = e.Subpath });
        cmd.Parameters.Add(new() { Value = e.IsActive });
        cmd.Parameters.Add(new() { Value = (object?)e.Slug ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(e.Tags));   // tags is NOT NULL
        cmd.Parameters.Add(new() { Value = e.CreatedAt == default ? DateTime.UtcNow : e.CreatedAt });
        cmd.Parameters.Add(new() { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new() { Value = e.OwnerShortname });
        cmd.Parameters.Add(new() { Value = (object?)e.OwnerGroupShortname ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Payload));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Relationships));
        cmd.Parameters.Add(new() { Value = (object?)e.LastChecksumHistory ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(e.ResourceType) });
        cmd.Parameters.Add(new() { Value = (object?)e.State ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)e.IsOpen ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Reporter));
        cmd.Parameters.Add(new() { Value = (object?)e.WorkflowShortname ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Collaborators));
        cmd.Parameters.Add(new() { Value = (object?)e.ResolutionReason ?? DBNull.Value });
        cmd.Parameters.Add(new()
        {
            Value = (e.QueryPolicies ?? new()).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string spaceName, string subpath, string shortname, ResourceType type, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM entries
            WHERE space_name = $1 AND subpath = $2 AND shortname = $3 AND resource_type = $4
            """, conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(type) });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task MoveAsync(Locator from, Locator to, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE entries
               SET space_name = $5, subpath = $6, shortname = $7, updated_at = NOW()
             WHERE space_name = $1 AND subpath = $2 AND shortname = $3 AND resource_type = $4
            """, conn);
        cmd.Parameters.Add(new() { Value = from.SpaceName });
        cmd.Parameters.Add(new() { Value = from.Subpath });
        cmd.Parameters.Add(new() { Value = from.Shortname });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(from.Type) });
        cmd.Parameters.Add(new() { Value = to.SpaceName });
        cmd.Parameters.Add(new() { Value = to.Subpath });
        cmd.Parameters.Add(new() { Value = to.Shortname });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<Entry>> QueryAsync(Query q, CancellationToken ct = default)
    {
        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args);

        // dmart's Query has both `sort_by` (field name) and `sort_type` (asc/desc).
        // For now we honor sort_type only and always sort by updated_at — proper
        // sort_by translation requires whitelisting safe column names.
        var sql = new System.Text.StringBuilder($"{SelectAllColumns} {where} ORDER BY updated_at ");
        sql.Append(q.SortType == Models.Enums.SortType.Ascending ? "ASC " : "DESC ");
        args.Add(new() { Value = Math.Max(1, q.Limit) });
        sql.Append($"LIMIT ${args.Count} ");
        args.Add(new() { Value = Math.Max(0, q.Offset) });
        sql.Append($"OFFSET ${args.Count}");

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Entry>();
        while (await reader.ReadAsync(ct)) results.Add(Hydrate(reader));
        return results;
    }

    // Counts the rows matching the same filters QueryAsync applies, WITHOUT
    // LIMIT/OFFSET. Used by QueryService to populate the Python-parity
    // `attributes.total` field on a query response (total matching rows),
    // which differs from the page count that `records` carries.
    public async Task<long> CountQueryAsync(Query q, CancellationToken ct = default)
    {
        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args);
        var sql = $"SELECT COUNT(*) FROM entries {where}";
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    // Assembles the WHERE clause used by both QueryAsync and CountQueryAsync
    // so they apply identical filters. Appends parameter values to `args`
    // and returns the `WHERE ... ` SQL fragment (or `WHERE space_name = $1 `
    // at a minimum — space_name is always required by the call sites).
    private static string BuildWhereClause(Query q, List<NpgsqlParameter> args)
    {
        var sql = new System.Text.StringBuilder("WHERE space_name = $1 ");
        args.Add(new() { Value = q.SpaceName });

        if (!string.IsNullOrEmpty(q.Subpath) && q.Subpath != "/")
        {
            args.Add(new() { Value = q.Subpath });
            sql.Append($"AND (subpath = ${args.Count} OR subpath LIKE ${args.Count} || '/%') ");
        }

        if (q.FilterTypes is { Count: > 0 })
        {
            args.Add(new()
            {
                Value = q.FilterTypes.Select(JsonbHelpers.EnumMember).ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            sql.Append($"AND resource_type = ANY(${args.Count}) ");
        }

        if (q.FilterShortnames is { Count: > 0 })
        {
            args.Add(new()
            {
                Value = q.FilterShortnames.ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            sql.Append($"AND shortname = ANY(${args.Count}) ");
        }

        if (q.FilterSchemaNames is { Count: > 0 })
        {
            // Python: "meta" is a sentinel in filter_schema_names meaning "don't
            // filter at all" — Python's adapter explicitly removes it and only
            // applies the filter if the remaining list is non-empty. Mirroring
            // that here so the default `filter_schema_names=["meta"]` behaves as
            // "no schema filter" instead of restricting to entries whose
            // schema_shortname is literally "meta" (which matches nothing).
            var effective = q.FilterSchemaNames.Where(n => n != "meta").ToArray();
            if (effective.Length > 0)
            {
                // dmart stores schema_shortname inside payload jsonb.
                args.Add(new()
                {
                    Value = effective,
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                sql.Append($"AND (payload->>'schema_shortname') = ANY(${args.Count}) ");
            }
        }

        if (q.FilterTags is { Count: > 0 })
        {
            // tags is JSONB, not array — use ?| operator (any element matches).
            // ?| takes a text[] on the right.
            args.Add(new()
            {
                Value = q.FilterTags.ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            sql.Append($"AND tags ?| ${args.Count} ");
        }

        if (!string.IsNullOrEmpty(q.Search))
        {
            // Substring search across the JSONB payload + displayname/description.
            args.Add(new() { Value = $"%{q.Search}%" });
            sql.Append($"AND (payload::text ILIKE ${args.Count} OR displayname::text ILIKE ${args.Count} OR description::text ILIKE ${args.Count}) ");
        }

        return sql.ToString();
    }

    public async Task<long> CountAsync(string spaceName, string subpath, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT COUNT(*) FROM entries
             WHERE space_name = $1 AND (subpath = $2 OR subpath LIKE $2 || '/%')
            """, conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    private static void AddJsonbNotNull(NpgsqlCommand cmd, string json)
    {
        cmd.Parameters.Add(new()
        {
            Value = json,
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
    }

    private static void AddJsonb(NpgsqlCommand cmd, string? json)
    {
        cmd.Parameters.Add(new()
        {
            Value = (object?)json ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
    }

    private static Entry Hydrate(NpgsqlDataReader r)
    {
        return new Entry
        {
            Uuid = r.GetGuid(0).ToString(),
            Shortname = r.GetString(1),
            SpaceName = r.GetString(2),
            Subpath = r.GetString(3),
            IsActive = r.GetBoolean(4),
            Slug = r.IsDBNull(5) ? null : r.GetString(5),
            Displayname = JsonbHelpers.FromTranslation(r.IsDBNull(6) ? null : r.GetString(6)),
            Description = JsonbHelpers.FromTranslation(r.IsDBNull(7) ? null : r.GetString(7)),
            Tags = JsonbHelpers.FromListString(r.IsDBNull(8) ? null : r.GetString(8)) ?? new(),
            CreatedAt = r.GetDateTime(9),
            UpdatedAt = r.GetDateTime(10),
            OwnerShortname = r.GetString(11),
            OwnerGroupShortname = r.IsDBNull(12) ? null : r.GetString(12),
            Acl = JsonbHelpers.FromAclList(r.IsDBNull(13) ? null : r.GetString(13)),
            Payload = JsonbHelpers.FromPayload(r.IsDBNull(14) ? null : r.GetString(14)),
            Relationships = JsonbHelpers.FromRelationships(r.IsDBNull(15) ? null : r.GetString(15)),
            LastChecksumHistory = r.IsDBNull(16) ? null : r.GetString(16),
            ResourceType = JsonbHelpers.ParseEnumMember<ResourceType>(r.GetString(17)),
            State = r.IsDBNull(18) ? null : r.GetString(18),
            IsOpen = r.IsDBNull(19) ? null : r.GetBoolean(19),
            Reporter = JsonbHelpers.FromReporter(r.IsDBNull(20) ? null : r.GetString(20)),
            WorkflowShortname = r.IsDBNull(21) ? null : r.GetString(21),
            Collaborators = JsonbHelpers.FromDictStringString(r.IsDBNull(22) ? null : r.GetString(22)),
            ResolutionReason = r.IsDBNull(23) ? null : r.GetString(23),
            QueryPolicies = r.IsDBNull(24) ? null : ((string[])r.GetValue(24)).ToList(),
        };
    }
}
