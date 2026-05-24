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
        await using var conn = await db.OpenAsync(ct);
        return await GetAsync(spaceName, subpath, shortname, type, conn, ct);
    }

    public async Task<Entry?> GetAsync(string spaceName, string subpath, string shortname, ResourceType type, NpgsqlConnection conn, CancellationToken ct = default)
    {
        // Try with the specified resource_type first (most callers know the type).
        // Scoped braces around the typed lookup so the reader+command release
        // the connection BEFORE the fallback issues a second command on it —
        // Npgsql forbids "command already in progress" on a shared session.
        Entry? typed = null;
        {
            await using var cmd = new NpgsqlCommand(
                $"{SelectAllColumns} WHERE space_name = $1 AND subpath = $2 AND shortname = $3 AND resource_type = $4",
                conn);
            cmd.Parameters.Add(new() { Value = spaceName });
            cmd.Parameters.Add(new() { Value = subpath });
            cmd.Parameters.Add(new() { Value = shortname });
            cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(type) });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) typed = Hydrate(reader);
        }
        if (typed is not null) return typed;

        // Fallback: the URL may specify a generic resource_type (e.g. "content")
        // but the actual row has a different type (e.g. "schema"). Since the
        // entries table UNIQUE constraint is (shortname, space_name, subpath),
        // resource_type is redundant for uniqueness. Python's SQL adapter
        // doesn't strictly filter by resource_type on single-entry loads —
        // the class_type parameter selects the Python model class, not the
        // SQL WHERE filter. Mirror that by retrying without the type filter.
        return await GetAsync(spaceName, subpath, shortname, conn, ct);
    }

    // Lookup without resource_type filter — used as fallback when the caller's
    // type hint doesn't match the actual row.
    public async Task<Entry?> GetAsync(string spaceName, string subpath, string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await GetAsync(spaceName, subpath, shortname, conn, ct);
    }

    public async Task<Entry?> GetAsync(string spaceName, string subpath, string shortname, NpgsqlConnection conn, CancellationToken ct = default)
    {
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
        // Deadlock-retry posture matches UserRepository — concurrent writes
        // to entries (typical when several plugins or imports run in
        // parallel) can trip Postgres' deadlock detector (SQLState 40P01)
        // or serialization failure (40001). Both are transient by design
        // and PG explicitly designs the application to retry. Bounded to
        // 3 attempts with a tiny randomised backoff to break symmetry.
        const int MaxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await db.OpenAsync(ct);
                await UpsertAsync(e, conn, ct);
                return;
            }
            catch (PostgresException ex) when (
                attempt < MaxAttempts &&
                (ex.SqlState == "40P01" || ex.SqlState == "40001"))
            {
                await Task.Delay(Random.Shared.Next(5, 25), ct);
            }
        }
    }

    public async Task UpsertAsync(Entry e, NpgsqlConnection conn, CancellationToken ct = default)
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

    // Atomic prior-fetch + upsert for the native-plugin save_entry path.
    // Returns the row as it stood before the write (null for inserts) and a
    // boolean indicating whether this call inserted vs updated. SELECT FOR
    // UPDATE locks the existing row (if any) so a concurrent REST or plugin
    // writer can't slip a write between our probe and our upsert — eliminates
    // the race window the two-statement variant used to expose.
    //
    // The "was this an INSERT vs UPDATE" signal comes from RETURNING
    // (xmax = 0): Postgres sets xmax to 0 on a freshly inserted tuple and to
    // a non-zero transaction id on an updated one, so the boolean reflects
    // exactly what the ON CONFLICT branch chose.
    //
    // Residual race: under READ COMMITTED, two concurrent inserts of the same
    // new key can both see "no prior" before the unique-index conflict
    // resolves. One inserts, one updates — the updater sees `inserted = false`
    // with a null prior and so writes no history row. That window is narrow
    // (plugin writers rarely race each other on a brand-new key) and is
    // acceptable for the plugin audit path. REST `EntryService.UpdateAsync`
    // already has a separately loaded prior via the service layer, so this
    // method exists specifically for the plugin callback.
    public async Task<(Entry? prior, bool inserted)> UpsertWithPriorAsync(Entry e, CancellationToken ct = default)
    {
        e = e with { QueryPolicies = Utils.QueryPolicies.Generate(e) };

        const int MaxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await UpsertWithPriorCoreAsync(e, ct);
            }
            catch (PostgresException ex) when (
                attempt < MaxAttempts &&
                (ex.SqlState == "40P01" || ex.SqlState == "40001"))
            {
                await Task.Delay(Random.Shared.Next(5, 25), ct);
            }
        }
    }

    private async Task<(Entry? prior, bool inserted)> UpsertWithPriorCoreAsync(Entry e, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the existing row (if any). The unique index is
        // (shortname, space_name, subpath) — same key the ON CONFLICT below
        // resolves on — so no resource_type filter; if the row exists under
        // a different type, that change should surface in the diff.
        Entry? prior = null;
        await using (var sel = new NpgsqlCommand(
            $"{SelectAllColumns} WHERE space_name = $1 AND subpath = $2 AND shortname = $3 FOR UPDATE",
            conn, tx))
        {
            sel.Parameters.Add(new() { Value = e.SpaceName });
            sel.Parameters.Add(new() { Value = e.Subpath });
            sel.Parameters.Add(new() { Value = e.Shortname });
            await using var reader = await sel.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) prior = Hydrate(reader);
        }

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
            RETURNING (xmax = 0) AS inserted
            """, conn, tx);

        cmd.Parameters.Add(new() { Value = Guid.Parse(e.Uuid) });
        cmd.Parameters.Add(new() { Value = e.Shortname });
        cmd.Parameters.Add(new() { Value = e.SpaceName });
        cmd.Parameters.Add(new() { Value = e.Subpath });
        cmd.Parameters.Add(new() { Value = e.IsActive });
        cmd.Parameters.Add(new() { Value = (object?)e.Slug ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(e.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(e.Tags));
        cmd.Parameters.Add(new() { Value = e.CreatedAt == default ? TimeUtils.Now() : e.CreatedAt });
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

        var raw = await cmd.ExecuteScalarAsync(ct);
        var inserted = raw is bool b && b;
        await tx.CommitAsync(ct);
        return (prior, inserted);
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

    // Move an entry (and, for folders, its entire subtree of entries +
    // attachments). All mutations are wrapped in a single transaction so a
    // failure leaves the source intact rather than half-moved. Returns the
    // number of entries that ended up at a new location (1 for non-folder
    // moves; 1 + descendants for folder moves).
    //
    // Three things happen for every moved entry that the prior single-UPDATE
    // implementation skipped:
    //
    //   1. query_policies is regenerated. The TEXT[] column on entries
    //      encodes the row's ACL fingerprint as a function of (space,
    //      subpath, resource_type, is_active, owner, owner_group); when
    //      subpath/space changes, the old patterns no longer match the
    //      caller's permission patterns, so without regen the moved entry
    //      becomes invisible at its new location (see UpsertAsync for
    //      the same regen-on-write pattern).
    //
    //   2. Attachments anchored to the entry follow it. Attachment rows
    //      anchor to their parent via subpath = parent_subpath/parent_shortname
    //      (AttachmentRepository.ListForParentAsync) — when the parent
    //      moves, the anchor breaks unless we re-anchor the children.
    //
    //   3. For folders, descendant entries (and their attachments) cascade.
    //      Without this, moving a folder leaves an orphaned subtree at the
    //      old path. Mirrors the cascade semantics of
    //      DeleteFolderTreeWithDependentsOnceAsync.
    //
    // Folder descendants are processed in uuid-keyset-paginated pages so
    // memory stays bounded regardless of subtree size — at most CHUNK_SIZE
    // entries are hydrated at any moment, rather than the whole subtree.
    //
    // The caller (EntryService.MoveAsync) hands us the loaded source Entry
    // so we don't need a re-fetch to compute the regenerated policies.
    public Task<int> MoveAsync(Entry source, Locator to, CancellationToken ct = default)
        => db.ExecuteWithRetryOnDeadlockAsync(c => MoveOnceAsync(source, to, c), ct);

    private async Task<int> MoveOnceAsync(Entry source, Locator to, CancellationToken ct)
    {
        // oldPrefix is what attachments + descendants anchor against today —
        // it's `source.Subpath/source.Shortname` (handling the root case where
        // source.Subpath == "/"). newPrefix is the same construction at the
        // destination. For a non-folder move there will be no descendant
        // entries (only attachments anchor at `oldPrefix`), so the same
        // expression works for both cases.
        var oldPrefix = source.Subpath == "/" ? "/" + source.Shortname : source.Subpath + "/" + source.Shortname;
        var newPrefix = to.Subpath == "/" ? "/" + to.Shortname : to.Subpath + "/" + to.Shortname;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. Update the source entry itself with regenerated query_policies.
        //    Single row, single statement — already optimal.
        var movedRoot = source with
        {
            SpaceName = to.SpaceName,
            Subpath = to.Subpath,
            Shortname = to.Shortname,
        };
        var rootPolicies = Utils.QueryPolicies.Generate(movedRoot).ToArray();
        int totalMoved;
        await using (var rootCmd = new NpgsqlCommand("""
            UPDATE entries
               SET space_name = $2, subpath = $3, shortname = $4,
                   query_policies = $5,
                   updated_at = NOW()
             WHERE uuid = $1
            """, conn, tx))
        {
            rootCmd.Parameters.Add(new() { Value = Guid.Parse(source.Uuid) });
            rootCmd.Parameters.Add(new() { Value = to.SpaceName });
            rootCmd.Parameters.Add(new() { Value = to.Subpath });
            rootCmd.Parameters.Add(new() { Value = to.Shortname });
            rootCmd.Parameters.Add(new()
            {
                Value = rootPolicies,
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            totalMoved = await rootCmd.ExecuteNonQueryAsync(ct);
        }

        // 2. For folder moves, translate every descendant entry — both direct
        //    children (subpath = oldPrefix) and deeper descendants
        //    (subpath LIKE oldPrefix/%). Each row's query_policies is
        //    regenerated in C# from its own owner/is_active/resource_type,
        //    then each page is shipped to Postgres via a single
        //    `UPDATE ... FROM (VALUES …)` statement. That collapses N
        //    per-row network round-trips to ⌈N/CHUNK_SIZE⌉ — the big-O win.
        //
        //    Reads are uuid-keyset-paginated rather than buffered. We hydrate
        //    at most CHUNK_SIZE entries at a time, so a folder with N
        //    descendants takes O(N) work but O(CHUNK_SIZE) memory regardless
        //    of N. The cursor (last uuid in the previous page) advances
        //    monotonically; combined with the predicate, already-translated
        //    rows fall out of subsequent SELECTs naturally.
        //
        //    CHUNK_SIZE caps each bulk-update's parameter count well under
        //    Postgres's 65535-parameter ceiling (4 params per row → 4000
        //    params per chunk) and bounds per-statement SQL text.
        if (source.ResourceType == ResourceType.Folder)
        {
            const int CHUNK_SIZE = 1000;
            var cursor = Guid.Empty;
            while (true)
            {
                var page = await ReadDescendantPageForMoveAsync(
                    conn, tx, source.SpaceName, oldPrefix, cursor, CHUNK_SIZE, ct);
                if (page.Count == 0) break;

                var updates = new List<(Guid Uuid, string NewSubpath, string[] NewPolicies)>(page.Count);
                foreach (var descendant in page)
                {
                    // The substring trick: descendant.Subpath either equals
                    // oldPrefix (direct child) or starts with oldPrefix + "/"
                    // (deeper). Stripping the oldPrefix prefix leaves "" or
                    // "/rest", which concatenated with newPrefix produces the
                    // correct translated subpath in both cases.
                    var newSubpath = newPrefix + descendant.Subpath[oldPrefix.Length..];
                    var movedDescendant = descendant with
                    {
                        SpaceName = to.SpaceName,
                        Subpath = newSubpath,
                    };
                    updates.Add((
                        Guid.Parse(descendant.Uuid),
                        newSubpath,
                        Utils.QueryPolicies.Generate(movedDescendant).ToArray()));
                }

                totalMoved += await BulkUpdateDescendantsAsync(
                    conn, tx, to.SpaceName, updates, 0, updates.Count, ct);
                cursor = Guid.Parse(page[^1].Uuid);
            }
        }

        // 3. Translate attachments rooted at oldPrefix. The (subpath = $2 OR
        //    LIKE $2/%) predicate is symmetric with DeleteUnderSubpathAsync —
        //    catches the parent's direct attachments (subpath = oldPrefix)
        //    plus, on a folder move, attachments owned by descendants
        //    (subpath LIKE oldPrefix/%). Attachments have no query_policies
        //    column, so nothing else to refresh here.
        await using (var cmd = new NpgsqlCommand("""
            UPDATE attachments
               SET space_name = $3,
                   subpath = $4 || substring(subpath FROM length($2) + 1),
                   updated_at = NOW()
             WHERE space_name = $1
               AND (subpath = $2 OR subpath LIKE $2 || '/%')
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = source.SpaceName });
            cmd.Parameters.Add(new() { Value = oldPrefix });
            cmd.Parameters.Add(new() { Value = to.SpaceName });
            cmd.Parameters.Add(new() { Value = newPrefix });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return totalMoved;
    }

    // Bulk-update one chunk of descendants: a single UPDATE statement that
    // joins entries against a literal VALUES table carrying (uuid, subpath,
    // policies) for every row in the chunk. One network round-trip regardless
    // of chunk size.
    //
    // The VALUES list is built dynamically because Postgres has no way to
    // bind a variable-length tuple list to a single parameter. Only integer
    // placeholder indices are concatenated into the SQL; every value flows
    // through NpgsqlCommand.Parameters — no caller-supplied string ends up
    // in the SQL text. Pattern matches ExistMaskAsync above.
    //
    // Caller is responsible for chunking so 1 + 3*len stays well below
    // Postgres's 65535-parameter limit.
    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: the dynamic VALUES list embeds only constant string fragments and integer placeholder indices. Every caller-supplied value flows through NpgsqlCommand.Parameters; no caller-supplied string is concatenated into the SQL.")]
    private static async Task<int> BulkUpdateDescendantsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string newSpaceName,
        List<(Guid Uuid, string NewSubpath, string[] NewPolicies)> updates,
        int offset, int len,
        CancellationToken ct)
    {
        if (len == 0) return 0;

        // ~80 chars per row in the VALUES clause + a fixed header.
        var sb = new System.Text.StringBuilder(120 + 80 * len);
        sb.Append("""
            UPDATE entries AS e
               SET space_name = $1,
                   subpath = v.subpath,
                   query_policies = v.policies,
                   updated_at = NOW()
              FROM (VALUES
            """);
        for (var i = 0; i < len; i++)
        {
            if (i > 0) sb.Append(',');
            // Param indexing: $1 = newSpaceName; then 3 params per row
            // starting at $2. Cast the first row explicitly so Postgres can
            // infer the VALUES column types; later rows inherit.
            var p = 2 + i * 3;
            sb.Append('(').Append('$').Append(p).Append("::uuid,$")
                          .Append(p + 1).Append("::text,$")
                          .Append(p + 2).Append("::text[])");
        }
        sb.Append(") AS v(uuid, subpath, policies) WHERE e.uuid = v.uuid");

        await using var cmd = new NpgsqlCommand(sb.ToString(), conn, tx);
        cmd.Parameters.Add(new() { Value = newSpaceName });
        for (var i = 0; i < len; i++)
        {
            var row = updates[offset + i];
            cmd.Parameters.Add(new() { Value = row.Uuid });
            cmd.Parameters.Add(new() { Value = row.NewSubpath });
            cmd.Parameters.Add(new()
            {
                Value = row.NewPolicies,
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // Page-fetch descendants for a folder move, keyset-paginated by uuid.
    // Caller passes the last uuid from the previous page (Guid.Empty for the
    // first page); we return the next pageSize rows in ascending uuid order
    // whose subpath still matches the source folder (either = folderPath or
    // LIKE folderPath/%). The keyset is monotone, so already-translated rows
    // — whose subpath no longer matches the predicate after their UPDATE —
    // are skipped naturally on the next pass.
    private static async Task<List<Entry>> ReadDescendantPageForMoveAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string spaceName, string folderPath, Guid cursor, int pageSize,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"{SelectAllColumns} WHERE space_name = $1 AND (subpath = $2 OR subpath LIKE $2 || '/%') AND uuid > $3 ORDER BY uuid LIMIT $4",
            conn, tx);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = folderPath });
        cmd.Parameters.Add(new() { Value = cursor });
        cmd.Parameters.Add(new() { Value = pageSize });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var page = new List<Entry>(pageSize);
        while (await r.ReadAsync(ct)) page.Add(Hydrate(r));
        return page;
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
