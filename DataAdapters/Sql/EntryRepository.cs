using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
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
        // Row-level ACL depends on entries.query_policies carrying the
        // owner/is_active/subpath fingerprints Python's generate_query_policies
        // would have written on the row. Without this, AppendAclFilter finds
        // no matching patterns and the entry becomes invisible to every
        // authenticated caller except the owner — even with correct
        // permissions. Always regenerate on upsert: the algorithm is
        // deterministic from (space, subpath, rt, is_active, owner,
        // owner_group), so a no-op change is still a no-op in the DB. When
        // those DO change (ownership transfer, toggle is_active) the new
        // policies reflect reality.
        e = e with { QueryPolicies = Utils.QueryPolicies.Generate(e) };

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
                updated_at = EXCLUDED.updated_at,
                owner_shortname = EXCLUDED.owner_shortname,
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
        cmd.Parameters.Add(new() { Value = e.CreatedAt == default ? TimeUtils.Now() : e.CreatedAt });
        // Honor the caller's UpdatedAt — normal create/update flows set it to
        // TimeUtils.Now() themselves, so behavior there is unchanged. The
        // import path needs to preserve the imported value so a round-trip
        // (export → delete → import) reproduces the row verbatim.
        cmd.Parameters.Add(new() { Value = e.UpdatedAt == default ? TimeUtils.Now() : e.UpdatedAt });
        cmd.Parameters.Add(new() { Value = e.OwnerShortname });
        cmd.Parameters.Add(new() { Value = (object?)e.OwnerGroupShortname ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Payload));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Relationships));
        cmd.Parameters.Add(new() { Value = (object?)e.LastChecksumHistory ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(e.ResourceType) });
        cmd.Parameters.Add(new() { Value = (object?)e.State ?? DBNull.Value });
#pragma warning disable CA1508 // Analyzer limitation: bool? boxed via (object?) cast IS null when source is null; the ?? is load-bearing.
        cmd.Parameters.Add(new() { Value = (object?)e.IsOpen ?? DBNull.Value });
#pragma warning restore CA1508
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

    // Batched existence probe used by EntryService.ValidateRelationshipsAsync.
    // Returns the set of (space, subpath, shortname) tuples present in
    // `entries`. Type is intentionally NOT part of the key — the entries
    // table UNIQUE constraint is (shortname, space_name, subpath) so a path
    // hit is enough; matches GetAsync(...)'s type-fallback semantics.
    //
    // One round trip per validate call instead of one per relationship.
    // SQL is `WHERE (space_name, subpath, shortname) IN ((...), ...)` with
    // 3*N positional parameters — safe to interpolate the placeholder list
    // because only integer indices are embedded; every caller-supplied value
    // binds through Npgsql parameter substitution.
    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: SQL placeholder list is built from integer indices; all caller-supplied values flow through NpgsqlCommand.Parameters.")]
    public async Task<HashSet<(string SpaceName, string Subpath, string Shortname)>> ExistMaskAsync(
        IReadOnlyList<(string SpaceName, string Subpath, string Shortname)> targets,
        CancellationToken ct = default)
    {
        var hits = new HashSet<(string, string, string)>();
        if (targets.Count == 0) return hits;

        await using var conn = await db.OpenAsync(ct);
        var sb = new System.Text.StringBuilder(
            "SELECT space_name, subpath, shortname FROM entries WHERE (space_name, subpath, shortname) IN (");
        await using var cmd = new NpgsqlCommand { Connection = conn };
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("($").Append(i * 3 + 1)
              .Append(",$").Append(i * 3 + 2)
              .Append(",$").Append(i * 3 + 3).Append(')');
            cmd.Parameters.Add(new() { Value = targets[i].SpaceName });
            cmd.Parameters.Add(new() { Value = targets[i].Subpath });
            cmd.Parameters.Add(new() { Value = targets[i].Shortname });
        }
        sb.Append(')');
        cmd.CommandText = sb.ToString();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            hits.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return hits;
    }

    // Returns the (space, subpath, shortname) of the first entry whose
    // relationships array contains a related_to matching the supplied
    // locator — or null if nothing references it. Used by the delete-time
    // RI gate so the caller can emit a useful error pointing at *which*
    // entry blocks the delete.
    //
    // Uses the `@>` JSONB containment operator: postgres considers
    // `[{"related_to":{type,space,subpath,shortname,…}}]` to contain
    // `[{"related_to":{type,space,subpath,shortname}}]` regardless of any
    // extra fields on the stored side. Without a GIN index this is a
    // sequential scan over `entries`; on tables of meaningful size, add
    // `CREATE INDEX entries_relationships_gin ON entries USING gin (relationships jsonb_path_ops)`
    // and the query degrades to an index lookup. We exclude the row being
    // deleted from the search so self-referencing entries are still
    // deletable.
    public async Task<(string SpaceName, string Subpath, string Shortname)?> FindFirstReferencerAsync(
        string targetSpace, string targetSubpath, string targetShortname, ResourceType targetType,
        string? excludeSpace, string? excludeSubpath, string? excludeShortname,
        CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // Build a one-element array containing the related_to skeleton.
        // EnumMember keeps the wire form ("content"/"ticket"/…) consistent
        // with how MaterializeEntry writes it.
        var probe = JsonSerializer.Serialize(new List<Dictionary<string, object>>
        {
            new()
            {
                ["related_to"] = new Dictionary<string, object>
                {
                    ["type"] = JsonbHelpers.EnumMember(targetType),
                    ["space_name"] = targetSpace,
                    ["subpath"] = targetSubpath,
                    ["shortname"] = targetShortname,
                },
            },
        }, DmartJsonContext.Default.ListDictionaryStringObject);

        await using var cmd = new NpgsqlCommand("""
            SELECT space_name, subpath, shortname
            FROM entries
            WHERE relationships @> $1::jsonb
              AND NOT (space_name = $2 AND subpath = $3 AND shortname = $4)
            LIMIT 1
            """, conn);
        cmd.Parameters.Add(new() { Value = probe });
        cmd.Parameters.Add(new() { Value = excludeSpace ?? string.Empty });
        cmd.Parameters.Add(new() { Value = excludeSubpath ?? string.Empty });
        cmd.Parameters.Add(new() { Value = excludeShortname ?? string.Empty });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    // Atomic, cascading folder delete. Removes (in one transaction):
    //   * histories rows for the folder + every descendant entry
    //   * locks rows for the folder + every descendant entry
    //   * attachments under the folder subtree
    //   * the folder row itself (at parent_subpath / folder_shortname)
    //   * every descendant entry (subpath = folderPath or LIKE folderPath/%)
    //
    // `folderPath` is the folder's full identity:
    //   parent_subpath == "/"  → "/{folder_shortname}"
    //   otherwise              → "{parent_subpath}/{folder_shortname}"
    //
    // Mirrors dmart Python adapter.py:2677-2696. Modeled after
    // SpaceRepository.DeleteOnceAsync — same template (one connection, one
    // transaction, dependent-table-first DELETE order, retry on deadlock).
    // Returns the number of `entries` rows deleted (folder + descendants).
    //
    // The `histories` and `locks` filters include an explicit clause for the
    // folder's own row (`subpath = parent_subpath AND shortname = folder`)
    // because those tables key on the entry's own subpath/shortname pair.
    // The `attachments` filter doesn't need that clause: attachment subpath
    // is always `{owner_subpath}/{owner_shortname}`, so `subpath = folderPath`
    // already catches attachments owned directly by the folder, and
    // `subpath LIKE folderPath || '/%'` catches everything deeper.
    public Task<int> DeleteFolderTreeWithDependentsAsync(
        string spaceName, string parentSubpath, string folderShortname,
        CancellationToken ct = default)
        => db.ExecuteWithRetryOnDeadlockAsync(
            c => DeleteFolderTreeWithDependentsOnceAsync(spaceName, parentSubpath, folderShortname, c),
            ct);

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: SQL is `\"DELETE FROM <const-table> WHERE \" + const-where`; only positional $1-$4 placeholders bind caller-supplied values.")]
    private async Task<int> DeleteFolderTreeWithDependentsOnceAsync(
        string spaceName, string parentSubpath, string folderShortname, CancellationToken ct)
    {
        var folderPath = parentSubpath == "/"
            ? "/" + folderShortname
            : parentSubpath + "/" + folderShortname;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // histories / locks: subpath/shortname identify the entry the row is
        // about. Three predicates: (folder's own row) ∪ (direct children at
        // subpath=folderPath) ∪ (deeper descendants at subpath LIKE folderPath/%).
        const string subtreeWithFolderRow = """
            space_name = $1
              AND ((subpath = $2 AND shortname = $3)
                OR  subpath = $4
                OR  subpath LIKE $4 || '/%')
            """;

        foreach (var sql in new[]
        {
            $"DELETE FROM histories WHERE {subtreeWithFolderRow}",
            $"DELETE FROM locks     WHERE {subtreeWithFolderRow}",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.Add(new() { Value = spaceName });
            cmd.Parameters.Add(new() { Value = parentSubpath });
            cmd.Parameters.Add(new() { Value = folderShortname });
            cmd.Parameters.Add(new() { Value = folderPath });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // attachments: subpath includes the owner's shortname, so the folder's
        // own attachments live at subpath = folderPath. No extra clause needed.
        await using (var cmd = new NpgsqlCommand("""
            DELETE FROM attachments
            WHERE space_name = $1
              AND (subpath = $2 OR subpath LIKE $2 || '/%')
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = spaceName });
            cmd.Parameters.Add(new() { Value = folderPath });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // entries: explicit `resource_type = 'folder'` guard on the folder
        // row protects against an unlikely-but-defensive case where a
        // non-folder entry happens to share (subpath, shortname) with the
        // folder we're deleting (e.g. a content entry called "widgets" in
        // the same parent as the folder "widgets").
        int entryRows;
        await using (var cmd = new NpgsqlCommand("""
            DELETE FROM entries
            WHERE space_name = $1
              AND ((subpath = $2 AND shortname = $3 AND resource_type = 'folder')
                OR  subpath = $4
                OR  subpath LIKE $4 || '/%')
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = spaceName });
            cmd.Parameters.Add(new() { Value = parentSubpath });
            cmd.Parameters.Add(new() { Value = folderShortname });
            cmd.Parameters.Add(new() { Value = folderPath });
            entryRows = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return entryRows;
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
