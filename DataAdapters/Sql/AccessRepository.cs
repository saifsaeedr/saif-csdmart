using Dmart.Models.Core;
using Dmart.Models.Enums;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// roles + permissions + userpermissionscache. Column-for-column mapping to dmart's
// Roles, Permissions, and UserPermissionsCache SQLModel tables.
public sealed class AccessRepository(Db db, AuthzCacheRefresher refresher)
{
    private const string SelectRoleColumns = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, acl, payload, relationships,
               last_checksum_history, resource_type, permissions, query_policies
        FROM roles
        """;

    private const string SelectPermissionColumns = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, acl, payload, relationships,
               last_checksum_history, resource_type, subpaths, resource_types,
               actions, conditions, restricted_fields, allowed_fields_values,
               filter_fields_values, query_policies
        FROM permissions
        """;

    public async Task<Role?> GetRoleAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectRoleColumns} WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? HydrateRole(r) : null;
    }

    public async Task<List<Role>> GetRolesAsync(IEnumerable<string> shortnames, CancellationToken ct = default)
    {
        var arr = shortnames.ToArray();
        if (arr.Length == 0) return new();
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectRoleColumns} WHERE shortname = ANY($1)", conn);
        cmd.Parameters.Add(new() { Value = arr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Role>();
        while (await r.ReadAsync(ct)) results.Add(HydrateRole(r));
        return results;
    }

    public async Task UpsertRoleAsync(Role role, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO roles (uuid, shortname, space_name, subpath, is_active, slug,
                               displayname, description, tags, created_at, updated_at,
                               owner_shortname, owner_group_shortname, acl, payload, relationships,
                               last_checksum_history, resource_type, permissions, query_policies)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20)
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
                permissions = EXCLUDED.permissions,
                query_policies = EXCLUDED.query_policies
            """, conn);

        cmd.Parameters.Add(new() { Value = Guid.Parse(role.Uuid) });
        cmd.Parameters.Add(new() { Value = role.Shortname });
        cmd.Parameters.Add(new() { Value = role.SpaceName });
        cmd.Parameters.Add(new() { Value = role.Subpath });
        cmd.Parameters.Add(new() { Value = role.IsActive });
        cmd.Parameters.Add(new() { Value = (object?)role.Slug ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(role.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(role.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(role.Tags));   // tags is NOT NULL
        cmd.Parameters.Add(new() { Value = role.CreatedAt == default ? DateTime.UtcNow : role.CreatedAt });
        cmd.Parameters.Add(new() { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new() { Value = role.OwnerShortname });
        cmd.Parameters.Add(new() { Value = (object?)role.OwnerGroupShortname ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(role.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(role.Payload));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(role.Relationships));
        cmd.Parameters.Add(new() { Value = (object?)role.LastChecksumHistory ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(role.ResourceType) });
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(role.Permissions));   // permissions is NOT NULL
        cmd.Parameters.Add(new()
        {
            Value = role.QueryPolicies.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });

        await cmd.ExecuteNonQueryAsync(ct);
        // role.permissions changed → mv_role_permissions is now stale.
        await refresher.RefreshAsync(ct);
    }

    public async Task<Permission?> GetPermissionAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectPermissionColumns} WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? HydratePermission(r) : null;
    }

    public async Task<List<Permission>> GetPermissionsAsync(IEnumerable<string> shortnames, CancellationToken ct = default)
    {
        var arr = shortnames.ToArray();
        if (arr.Length == 0) return new();
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectPermissionColumns} WHERE shortname = ANY($1)", conn);
        cmd.Parameters.Add(new() { Value = arr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Permission>();
        while (await r.ReadAsync(ct)) results.Add(HydratePermission(r));
        return results;
    }

    public async Task UpsertPermissionAsync(Permission p, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO permissions (uuid, shortname, space_name, subpath, is_active, slug,
                                     displayname, description, tags, created_at, updated_at,
                                     owner_shortname, owner_group_shortname, acl, payload, relationships,
                                     last_checksum_history, resource_type, subpaths, resource_types,
                                     actions, conditions, restricted_fields, allowed_fields_values,
                                     filter_fields_values, query_policies)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25,$26)
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
                subpaths = EXCLUDED.subpaths,
                resource_types = EXCLUDED.resource_types,
                actions = EXCLUDED.actions,
                conditions = EXCLUDED.conditions,
                restricted_fields = EXCLUDED.restricted_fields,
                allowed_fields_values = EXCLUDED.allowed_fields_values,
                filter_fields_values = EXCLUDED.filter_fields_values,
                query_policies = EXCLUDED.query_policies
            """, conn);

        cmd.Parameters.Add(new() { Value = Guid.Parse(p.Uuid) });
        cmd.Parameters.Add(new() { Value = p.Shortname });
        cmd.Parameters.Add(new() { Value = p.SpaceName });
        cmd.Parameters.Add(new() { Value = p.Subpath });
        cmd.Parameters.Add(new() { Value = p.IsActive });
        cmd.Parameters.Add(new() { Value = (object?)p.Slug ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(p.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(p.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(p.Tags));   // tags is NOT NULL
        cmd.Parameters.Add(new() { Value = p.CreatedAt == default ? DateTime.UtcNow : p.CreatedAt });
        cmd.Parameters.Add(new() { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new() { Value = p.OwnerShortname });
        cmd.Parameters.Add(new() { Value = (object?)p.OwnerGroupShortname ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(p.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(p.Payload));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(p.Relationships));
        cmd.Parameters.Add(new() { Value = (object?)p.LastChecksumHistory ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(p.ResourceType) });
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbDict(p.Subpaths));               // NOT NULL
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(p.ResourceTypes));          // NOT NULL
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(p.Actions));                // NOT NULL
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(p.Conditions));             // NOT NULL
        AddJsonb(cmd, JsonbHelpers.ToJsonb(p.RestrictedFields));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(p.AllowedFieldsValues));
        cmd.Parameters.Add(new() { Value = (object?)p.FilterFieldsValues ?? DBNull.Value });
        cmd.Parameters.Add(new()
        {
            Value = p.QueryPolicies.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });

        await cmd.ExecuteNonQueryAsync(ct);
        // permission shape changed → invalidate cached resolutions; the materialized
        // views key off role.permissions which doesn't change here, so just clear cache.
        await InvalidateAllCachesAsync(ct);
    }

    // Returns true when a row was deleted, false when no matching role existed.
    // Invalidates the MV + in-memory permission caches on success so permission
    // decisions don't keep hitting stale mv_role_permissions/mv_user_roles rows.
    public async Task<bool> DeleteRoleAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM roles WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0) return false;
        await refresher.RefreshAsync(ct);
        await InvalidateAllCachesAsync(ct);
        return true;
    }

    public async Task<bool> DeletePermissionAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM permissions WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0) return false;
        await refresher.RefreshAsync(ct);
        await InvalidateAllCachesAsync(ct);
        return true;
    }

    // ----- query support (used by QueryService for management/roles and management/permissions) -----

    public Task<List<Role>> QueryRolesAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectRoleColumns, q, HydrateRole, ct);

    public Task<int> CountRolesQueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "roles", q, ct);

    public Task<List<Permission>> QueryPermissionsAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectPermissionColumns, q, HydratePermission, ct);

    public Task<int> CountPermissionsQueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "permissions", q, ct);

    // Reads the cached permissions dict from userpermissionscache.
    // Returns null if not cached — caller should generate + cache.
    public async Task<Dictionary<string, object>?> GetCachedUserPermissionsAsync(string userShortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT permissions FROM userpermissionscache WHERE user_shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = userShortname });
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        return json is not null ? JsonbHelpers.FromDictStringObject(json) : null;
    }

    // Generates the Python-parity permissions dict by walking
    // user → roles → permissions and building:
    //   { "space:subpath:resource_type": { allowed_actions, conditions, ... } }
    // Then caches it in userpermissionscache for subsequent calls.
    public async Task<Dictionary<string, object>> GenerateUserPermissionsAsync(
        string userShortname, CancellationToken ct = default)
    {
        // Check cache first.
        var cached = await GetCachedUserPermissionsAsync(userShortname, ct);
        if (cached is not null) return cached;

        // Walk: user.roles → role.permissions → permission rows.
        var user = await new UserRepository(db, refresher).GetByShortnameAsync(userShortname, ct);
        if (user is null) return new();

        var roles = user.Roles.Count > 0 ? await GetRolesAsync(user.Roles, ct) : new();
        var permNames = roles.SelectMany(r => r.Permissions).Distinct().ToArray();
        var perms = permNames.Length > 0 ? await GetPermissionsAsync(permNames, ct) : new();

        // Build the dict matching Python's generate_user_permissions output.
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var perm in perms)
        {
            foreach (var (spaceName, subpaths) in perm.Subpaths)
            {
                var subpathList = subpaths.Count > 0 ? subpaths : new List<string> { "/" };
                foreach (var subpath in subpathList)
                {
                    foreach (var rt in perm.ResourceTypes)
                    {
                        var key = $"{spaceName}:{subpath}:{rt}";
                        var entry = new Dictionary<string, object>
                        {
                            ["allowed_actions"] = perm.Actions,
                            ["conditions"] = perm.Conditions,
                            ["restricted_fields"] = perm.RestrictedFields ?? (object)new List<string>(),
                            ["allowed_fields_values"] = perm.AllowedFieldsValues ?? (object)new Dictionary<string, object>(),
                            ["filter_fields_values"] = perm.FilterFieldsValues ?? (object)"",
                        };
                        result[key] = entry;
                    }
                }
            }
        }

        // Cache for next time.
        await CacheUserPermissionsAsync(userShortname, result, ct);
        return result;
    }

    public async Task CacheUserPermissionsAsync(string userShortname, Dictionary<string, object> resolved, CancellationToken ct = default)
    {
        var json = JsonbHelpers.ToJsonb(resolved);
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO userpermissionscache (user_shortname, permissions)
            VALUES ($1, $2)
            ON CONFLICT (user_shortname) DO UPDATE SET permissions = EXCLUDED.permissions
            """, conn);
        cmd.Parameters.Add(new() { Value = userShortname });
        cmd.Parameters.Add(new() { Value = (object?)json ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InvalidateAllCachesAsync(CancellationToken ct = default)
    {
        // Clear the process-local cache the PermissionService consults on every
        // request — otherwise a permission/role write would only invalidate the
        // DB-side cache table while in-process workers keep serving stale decisions.
        refresher.InvalidateAllInMemory();

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM userpermissionscache", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddJsonb(NpgsqlCommand cmd, string? json)
        => cmd.Parameters.Add(new() { Value = (object?)json ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });

    private static void AddJsonbNotNull(NpgsqlCommand cmd, string json)
        => cmd.Parameters.Add(new() { Value = json, NpgsqlDbType = NpgsqlDbType.Jsonb });

    private static Role HydrateRole(NpgsqlDataReader r)
    {
        return new Role
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
            Permissions = JsonbHelpers.FromListString(r.IsDBNull(18) ? null : r.GetString(18)) ?? new(),
            QueryPolicies = r.IsDBNull(19) ? new() : ((string[])r.GetValue(19)).ToList(),
        };
    }

    private static Permission HydratePermission(NpgsqlDataReader r)
    {
        return new Permission
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
            Subpaths = JsonbHelpers.FromDictListString(r.IsDBNull(18) ? null : r.GetString(18)) ?? new(),
            ResourceTypes = JsonbHelpers.FromListString(r.IsDBNull(19) ? null : r.GetString(19)) ?? new(),
            Actions = JsonbHelpers.FromListString(r.IsDBNull(20) ? null : r.GetString(20)) ?? new(),
            Conditions = JsonbHelpers.FromListString(r.IsDBNull(21) ? null : r.GetString(21)) ?? new(),
            RestrictedFields = JsonbHelpers.FromListString(r.IsDBNull(22) ? null : r.GetString(22)),
            AllowedFieldsValues = JsonbHelpers.FromDictStringObject(r.IsDBNull(23) ? null : r.GetString(23)),
            FilterFieldsValues = r.IsDBNull(24) ? null : r.GetString(24),
            QueryPolicies = r.IsDBNull(25) ? new() : ((string[])r.GetValue(25)).ToList(),
        };
    }
}
