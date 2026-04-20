using Dmart.Models.Core;
using Dmart.Models.Enums;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// Mirrors UserRepository / AccessRepository pattern for the `spaces` table.
// Spaces are first-class top-level entities in dmart — every space has its own row
// in this table and acts as the namespace for entries/attachments/etc.
public sealed class SpaceRepository(Db db)
{
    private const string SelectAllColumns = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, acl, payload, relationships,
               last_checksum_history, resource_type,
               root_registration_signature, primary_website, indexing_enabled,
               capture_misses, check_health, languages, icon, mirrors,
               hide_folders, hide_space, active_plugins, ordinal, query_policies
        FROM spaces
        """;

    public async Task<Space?> GetAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectAllColumns} WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Hydrate(r) : null;
    }

    public async Task<List<Space>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectAllColumns} ORDER BY shortname", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Space>();
        while (await r.ReadAsync(ct)) results.Add(Hydrate(r));
        return results;
    }

    public async Task UpsertAsync(Space space, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO spaces (uuid, shortname, space_name, subpath, is_active, slug,
                                displayname, description, tags, created_at, updated_at,
                                owner_shortname, owner_group_shortname, acl, payload, relationships,
                                last_checksum_history, resource_type,
                                root_registration_signature, primary_website, indexing_enabled,
                                capture_misses, check_health, languages, icon, mirrors,
                                hide_folders, hide_space, active_plugins, ordinal, query_policies)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25,$26,$27,$28,$29,$30,$31)
            -- dmart's spaces table has its UNIQUE constraint on the full Unique tuple
            -- (shortname, space_name, subpath), not on shortname alone.
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
                root_registration_signature = EXCLUDED.root_registration_signature,
                primary_website = EXCLUDED.primary_website,
                indexing_enabled = EXCLUDED.indexing_enabled,
                capture_misses = EXCLUDED.capture_misses,
                check_health = EXCLUDED.check_health,
                languages = EXCLUDED.languages,
                icon = EXCLUDED.icon,
                mirrors = EXCLUDED.mirrors,
                hide_folders = EXCLUDED.hide_folders,
                hide_space = EXCLUDED.hide_space,
                active_plugins = EXCLUDED.active_plugins,
                ordinal = EXCLUDED.ordinal,
                query_policies = EXCLUDED.query_policies
            """, conn);

        cmd.Parameters.Add(new() { Value = Guid.Parse(space.Uuid) });
        cmd.Parameters.Add(new() { Value = space.Shortname });
        cmd.Parameters.Add(new() { Value = space.SpaceName });
        cmd.Parameters.Add(new() { Value = space.Subpath });
        cmd.Parameters.Add(new() { Value = space.IsActive });
        cmd.Parameters.Add(new() { Value = (object?)space.Slug ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(space.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(space.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(space.Tags));
        cmd.Parameters.Add(new() { Value = space.CreatedAt == default ? DateTime.UtcNow : space.CreatedAt });
        cmd.Parameters.Add(new() { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new() { Value = space.OwnerShortname });
        cmd.Parameters.Add(new() { Value = (object?)space.OwnerGroupShortname ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(space.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(space.Payload));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(space.Relationships));
        cmd.Parameters.Add(new() { Value = (object?)space.LastChecksumHistory ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(space.ResourceType) });
        cmd.Parameters.Add(new() { Value = space.RootRegistrationSignature });
        cmd.Parameters.Add(new() { Value = space.PrimaryWebsite });
        cmd.Parameters.Add(new() { Value = space.IndexingEnabled });
        cmd.Parameters.Add(new() { Value = space.CaptureMisses });
        cmd.Parameters.Add(new() { Value = space.CheckHealth });
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbLanguagesNotNull(space.Languages));
        cmd.Parameters.Add(new() { Value = space.Icon });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(space.Mirrors));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(space.HideFolders));
        cmd.Parameters.Add(new() { Value = (object?)space.HideSpace ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(space.ActivePlugins));
        cmd.Parameters.Add(new() { Value = (object?)space.Ordinal ?? DBNull.Value });
        cmd.Parameters.Add(new()
        {
            Value = space.QueryPolicies.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Cascade-deletes everything belonging to the space (entries, attachments,
    // locks, histories) before removing the spaces row itself, all in one transaction.
    // Matches dmart Python's "delete space" semantics where the entire namespace
    // disappears with the space.
    //
    // Wrapped in Db.ExecuteWithRetryOnDeadlockAsync to handle the race with
    // the fire-and-forget resource_folders_creation plugin (see
    // Api/Managed/RequestHandler.cs): a client deleting a just-created space
    // can race the plugin's folder-insertion transaction and PostgreSQL
    // aborts one with 40P01. Deadlocks are transient and retry-safe.
    public Task<bool> DeleteAsync(string shortname, CancellationToken ct = default)
        => db.ExecuteWithRetryOnDeadlockAsync(c => DeleteOnceAsync(shortname, c), ct);

    private async Task<bool> DeleteOnceAsync(string shortname, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var statements = new[]
        {
            "DELETE FROM histories   WHERE space_name = $1",
            "DELETE FROM locks       WHERE space_name = $1",
            "DELETE FROM attachments WHERE space_name = $1",
            "DELETE FROM entries     WHERE space_name = $1",
            "DELETE FROM spaces      WHERE shortname  = $1",
        };

        var rowsDeletedFromSpaces = 0;
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.Add(new() { Value = shortname });
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (sql.Contains("FROM spaces", StringComparison.Ordinal)) rowsDeletedFromSpaces = n;
        }

        await tx.CommitAsync(ct);
        return rowsDeletedFromSpaces > 0;
    }

    private static void AddJsonb(NpgsqlCommand cmd, string? json)
        => cmd.Parameters.Add(new() { Value = (object?)json ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });

    private static void AddJsonbNotNull(NpgsqlCommand cmd, string json)
        => cmd.Parameters.Add(new() { Value = json, NpgsqlDbType = NpgsqlDbType.Jsonb });

    private static Space Hydrate(NpgsqlDataReader r)
    {
        return new Space
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
            RootRegistrationSignature = r.GetString(18),
            PrimaryWebsite = r.GetString(19),
            IndexingEnabled = r.GetBoolean(20),
            CaptureMisses = r.GetBoolean(21),
            CheckHealth = r.GetBoolean(22),
            Languages = JsonbHelpers.FromLanguages(r.IsDBNull(23) ? null : r.GetString(23)) ?? new(),
            Icon = r.GetString(24),
            Mirrors = JsonbHelpers.FromListString(r.IsDBNull(25) ? null : r.GetString(25)),
            HideFolders = JsonbHelpers.FromListString(r.IsDBNull(26) ? null : r.GetString(26)),
            HideSpace = r.IsDBNull(27) ? null : r.GetBoolean(27),
            ActivePlugins = JsonbHelpers.FromListString(r.IsDBNull(28) ? null : r.GetString(28)),
            Ordinal = r.IsDBNull(29) ? null : r.GetInt32(29),
            QueryPolicies = r.IsDBNull(30) ? new() : ((string[])r.GetValue(30)).ToList(),
        };
    }
}
