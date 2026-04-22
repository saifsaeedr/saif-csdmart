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
        // Try with the specified resource_type first (most callers know the type).
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"{SelectAllColumns} WHERE space_name = $1 AND subpath = $2 AND shortname = $3 AND resource_type = $4",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(type) });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct)) return Hydrate(reader);

        // Fallback: the URL may specify a generic resource_type (e.g. "content")
        // but the actual row has a different type (e.g. "schema"). Since the
        // entries table UNIQUE constraint is (shortname, space_name, subpath),
        // resource_type is redundant for uniqueness. Python's SQL adapter
        // doesn't strictly filter by resource_type on single-entry loads —
        // the class_type parameter selects the Python model class, not the
        // SQL WHERE filter. Mirror that by retrying without the type filter.
        return await GetAsync(spaceName, subpath, shortname, ct);
    }

    // Lookup without resource_type filter — used as fallback when the caller's
    // type hint doesn't match the actual row.
    public async Task<Entry?> GetAsync(string spaceName, string subpath, string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"{SelectAllColumns} WHERE space_name = $1 AND subpath = $2 AND shortname = $3",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
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

    public Task<List<Entry>> QueryAsync(Query q, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectAllColumns, q, Hydrate, ct, tableName: "entries");

    // Authenticated-actor overload — applies owner/ACL/query_policies
    // filtering so rows the caller can't see are excluded. Callers with
    // no actor context (internal plugins, import/export) should use the
    // parameterless overload above and remain server-unrestricted.
    public Task<List<Entry>> QueryAsync(Query q, string actor, List<string>? queryPolicies, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectAllColumns, q, Hydrate, ct,
            tableName: "entries", userShortname: actor, queryPolicies: queryPolicies);

    public Task<int> CountQueryAsync(Query q, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "entries", q, ct);

    public Task<int> CountQueryAsync(Query q, string actor, List<string>? queryPolicies, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "entries", q, ct,
            userShortname: actor, queryPolicies: queryPolicies);

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
