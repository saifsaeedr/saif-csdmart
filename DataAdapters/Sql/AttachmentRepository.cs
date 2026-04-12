using Dmart.Models.Core;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// dmart's Attachments table inherits from Metas — same Unique base. The "parent" is
// expressed via the (space_name, subpath, shortname) of the attachment row, where the
// subpath includes the parent shortname (e.g. /content/foo/.attachments). We follow
// dmart's convention here.
public sealed class AttachmentRepository(Db db)
{
    private const string SelectAllColumns = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, acl, payload, relationships,
               last_checksum_history, resource_type, media, body, state
        FROM attachments
        """;

    public async Task<List<Attachment>> ListForParentAsync(string spaceName, string parentSubpath, string parentShortname, CancellationToken ct = default)
    {
        var attachmentSubpath = $"{parentSubpath.TrimEnd('/')}/{parentShortname}";
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"{SelectAllColumns} WHERE space_name = $1 AND subpath = $2 ORDER BY created_at DESC", conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = attachmentSubpath });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Attachment>();
        while (await r.ReadAsync(ct)) results.Add(Hydrate(r));
        return results;
    }

    // Direct lookup by (space, subpath, shortname) — used by /managed/payload/... when
    // the URL already points at the attachment row itself.
    public async Task<Attachment?> GetAsync(string spaceName, string subpath, string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"{SelectAllColumns} WHERE space_name = $1 AND subpath = $2 AND shortname = $3", conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = Locator.NormalizeSubpath(subpath) });
        cmd.Parameters.Add(new() { Value = shortname });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Hydrate(r) : null;
    }

    public async Task<Attachment?> GetByUuidAsync(Guid uuid, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectAllColumns} WHERE uuid = $1", conn);
        cmd.Parameters.Add(new() { Value = uuid });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Hydrate(r) : null;
    }

    public async Task UpsertAsync(Attachment a, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO attachments (uuid, shortname, space_name, subpath, is_active, slug,
                                     displayname, description, tags, created_at, updated_at,
                                     owner_shortname, owner_group_shortname, acl, payload, relationships,
                                     last_checksum_history, resource_type, media, body, state)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21)
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
                media = EXCLUDED.media,
                body = EXCLUDED.body,
                state = EXCLUDED.state
            """, conn);

        cmd.Parameters.Add(new() { Value = Guid.Parse(a.Uuid) });
        cmd.Parameters.Add(new() { Value = a.Shortname });
        cmd.Parameters.Add(new() { Value = a.SpaceName });
        cmd.Parameters.Add(new() { Value = a.Subpath });
        cmd.Parameters.Add(new() { Value = a.IsActive });
        cmd.Parameters.Add(new() { Value = (object?)a.Slug ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(a.Tags));   // tags is NOT NULL
        cmd.Parameters.Add(new() { Value = a.CreatedAt == default ? DateTime.UtcNow : a.CreatedAt });
        cmd.Parameters.Add(new() { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new() { Value = a.OwnerShortname });
        cmd.Parameters.Add(new() { Value = (object?)a.OwnerGroupShortname ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Payload));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Relationships));
        cmd.Parameters.Add(new() { Value = (object?)a.LastChecksumHistory ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(a.ResourceType) });
        cmd.Parameters.Add(new() { Value = (object?)a.Media ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new() { Value = (object?)a.Body ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)a.State ?? DBNull.Value });

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(byte[]? Bytes, string? ContentType)> GetMediaAsync(Guid uuid, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT media, payload FROM attachments WHERE uuid = $1", conn);
        cmd.Parameters.Add(new() { Value = uuid });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null);
        var bytes = r.IsDBNull(0) ? null : (byte[])r.GetValue(0);
        var payload = JsonbHelpers.FromPayload(r.IsDBNull(1) ? null : r.GetString(1));
        var contentType = payload?.ContentType.ToString().ToLowerInvariant();
        return (bytes, contentType);
    }

    public async Task DeleteAsync(Guid uuid, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM attachments WHERE uuid = $1", conn);
        cmd.Parameters.Add(new() { Value = uuid });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ----- query support (used by QueryService for type=attachments) -----

    public Task<List<Attachment>> QueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectAllColumns, q, Hydrate, ct);

    public Task<int> CountQueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "attachments", q, ct);

    private static void AddJsonb(NpgsqlCommand cmd, string? json)
        => cmd.Parameters.Add(new() { Value = (object?)json ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });

    private static void AddJsonbNotNull(NpgsqlCommand cmd, string json)
        => cmd.Parameters.Add(new() { Value = json, NpgsqlDbType = NpgsqlDbType.Jsonb });

    private static Attachment Hydrate(NpgsqlDataReader r)
    {
        return new Attachment
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
            ResourceType = JsonbHelpers.ParseEnumMember<Models.Enums.ResourceType>(r.GetString(17)),
            Media = r.IsDBNull(18) ? null : (byte[])r.GetValue(18),
            Body = r.IsDBNull(19) ? null : r.GetString(19),
            State = r.IsDBNull(20) ? null : r.GetString(20),
        };
    }
}
