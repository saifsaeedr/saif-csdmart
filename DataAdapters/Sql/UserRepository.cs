using Dmart.Models.Core;
using Dmart.Models.Enums;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

public sealed class UserRepository(Db db, AuthzCacheRefresher refresher)
{
    private const string SelectAllColumns = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, payload,
               last_checksum_history, resource_type,
               password, roles, groups, acl, relationships,
               type::text, language::text, email, msisdn, locked_to_device,
               is_email_verified, is_msisdn_verified, force_password_change,
               device_id, google_id, facebook_id, social_avatar_url,
               attempt_count, last_login, notes, query_policies
        FROM users
        """;

    public async Task<User?> GetByShortnameAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectAllColumns} WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectAllColumns} WHERE LOWER(email) = LOWER($1)", conn);
        cmd.Parameters.Add(new() { Value = email });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    public async Task<User?> GetByMsisdnAsync(string msisdn, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"{SelectAllColumns} WHERE msisdn = $1", conn);
        cmd.Parameters.Add(new() { Value = msisdn });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    public async Task<bool> ExistsAsync(string? shortname, string? email, string? msisdn, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT 1 FROM users
             WHERE ($1::text IS NOT NULL AND shortname = $1)
                OR ($2::text IS NOT NULL AND LOWER(email) = LOWER($2))
                OR ($3::text IS NOT NULL AND msisdn = $3)
             LIMIT 1
            """, conn);
        cmd.Parameters.Add(new() { Value = (object?)shortname ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)email ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)msisdn ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task UpsertAsync(User u, CancellationToken ct = default)
    {
        // Populate query_policies deterministically on every write so the
        // row-level ACL filter (QueryHelper.AppendAclFilter) can match
        // patterns against it. See EntryRepository.UpsertAsync for the
        // full rationale — same pattern, same invariant.
        u = u with { QueryPolicies = Utils.QueryPolicies.Generate(u) };

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO users (uuid, shortname, space_name, subpath, is_active, slug,
                               displayname, description, tags, created_at, updated_at,
                               owner_shortname, owner_group_shortname, payload,
                               last_checksum_history, resource_type,
                               password, roles, groups, acl, relationships,
                               type, language, email, msisdn, locked_to_device,
                               is_email_verified, is_msisdn_verified, force_password_change,
                               device_id, google_id, facebook_id, social_avatar_url,
                               attempt_count, last_login, notes, query_policies)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,
                    $22::usertype,$23::language,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34,$35,$36,$37)
            ON CONFLICT (shortname) DO UPDATE SET
                space_name = EXCLUDED.space_name,
                subpath = EXCLUDED.subpath,
                is_active = EXCLUDED.is_active,
                slug = EXCLUDED.slug,
                displayname = EXCLUDED.displayname,
                description = EXCLUDED.description,
                tags = EXCLUDED.tags,
                updated_at = NOW(),
                owner_group_shortname = EXCLUDED.owner_group_shortname,
                payload = EXCLUDED.payload,
                last_checksum_history = EXCLUDED.last_checksum_history,
                password = EXCLUDED.password,
                roles = EXCLUDED.roles,
                groups = EXCLUDED.groups,
                acl = EXCLUDED.acl,
                relationships = EXCLUDED.relationships,
                type = EXCLUDED.type,
                language = EXCLUDED.language,
                email = EXCLUDED.email,
                msisdn = EXCLUDED.msisdn,
                locked_to_device = EXCLUDED.locked_to_device,
                is_email_verified = EXCLUDED.is_email_verified,
                is_msisdn_verified = EXCLUDED.is_msisdn_verified,
                force_password_change = EXCLUDED.force_password_change,
                device_id = EXCLUDED.device_id,
                google_id = EXCLUDED.google_id,
                facebook_id = EXCLUDED.facebook_id,
                social_avatar_url = EXCLUDED.social_avatar_url,
                attempt_count = EXCLUDED.attempt_count,
                last_login = EXCLUDED.last_login,
                notes = EXCLUDED.notes,
                query_policies = EXCLUDED.query_policies
            """, conn);

        cmd.Parameters.Add(new() { Value = Guid.Parse(u.Uuid) });
        cmd.Parameters.Add(new() { Value = u.Shortname });
        cmd.Parameters.Add(new() { Value = u.SpaceName });
        cmd.Parameters.Add(new() { Value = u.Subpath });
        cmd.Parameters.Add(new() { Value = u.IsActive });
        cmd.Parameters.Add(new() { Value = (object?)u.Slug ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Tags));   // tags is NOT NULL
        cmd.Parameters.Add(new() { Value = u.CreatedAt == default ? DateTime.UtcNow : u.CreatedAt });
        cmd.Parameters.Add(new() { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new() { Value = u.OwnerShortname });
        cmd.Parameters.Add(new() { Value = (object?)u.OwnerGroupShortname ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Payload));
        cmd.Parameters.Add(new() { Value = (object?)u.LastChecksumHistory ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumMember(u.ResourceType) });
        cmd.Parameters.Add(new() { Value = (object?)u.Password ?? DBNull.Value });
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Roles));   // roles is NOT NULL
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Groups));  // groups is NOT NULL
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Relationships));
        // PG enum values: usertype='web'/'mobile'/'bot', language='ar'/'en'/'ku'/'fr'/'tr'.
        // Both match the C# enum member names lowercased (UserType.Web→"web", Language.En→"en").
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumNameLower(u.Type) });
        cmd.Parameters.Add(new() { Value = JsonbHelpers.EnumNameLower(u.Language) });
        cmd.Parameters.Add(new() { Value = (object?)u.Email ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)u.Msisdn ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = u.LockedToDevice });
        cmd.Parameters.Add(new() { Value = u.IsEmailVerified });
        cmd.Parameters.Add(new() { Value = u.IsMsisdnVerified });
        cmd.Parameters.Add(new() { Value = u.ForcePasswordChange });
        cmd.Parameters.Add(new() { Value = (object?)u.DeviceId ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)u.GoogleId ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)u.FacebookId ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)u.SocialAvatarUrl ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = (object?)u.AttemptCount ?? DBNull.Value });
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.LastLogin));
        cmd.Parameters.Add(new() { Value = (object?)u.Notes ?? DBNull.Value });
        cmd.Parameters.Add(new()
        {
            Value = u.QueryPolicies.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });

        await cmd.ExecuteNonQueryAsync(ct);
        // user.roles may have changed → clear the in-memory permission cache.
        await refresher.RefreshAsync(ct);
    }

    public async Task DeleteAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM users WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await cmd.ExecuteNonQueryAsync(ct);
        await refresher.RefreshAsync(ct);
    }

    public async Task IncrementAttemptAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE users SET attempt_count = COALESCE(attempt_count, 0) + 1 WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ResetAttemptsAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE users SET attempt_count = 0 WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ----- sessions -----
    // `firebaseToken` is optional — Python persists it on the session row at
    // login time so downstream push-notification code can fan out to every
    // active session without a per-session update cycle. The C# port doesn't
    // ship a push sender (out of scope), but the row must still be written so
    // a future plugin has data to read via GetSessionFirebaseTokensAsync.
    public async Task CreateSessionAsync(
        string shortname, string token, string? firebaseToken = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO sessions (uuid, shortname, token, firebase_token, timestamp)
            VALUES (gen_random_uuid(), $1, $2, $3, NOW())
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = (object?)firebaseToken ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Update the firebase_token on exactly one session row — identified by
    // (shortname, token). Mirrors Python's db.update_session_firebase_token()
    // in backend/data_adapters/sql/adapter.py. Called from the profile update
    // flow when the caller PATCHes `firebase_token` on /user/profile.
    public async Task UpdateSessionFirebaseTokenAsync(
        string shortname, string token, string firebaseToken, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE sessions SET firebase_token = $3
            WHERE shortname = $1 AND token = $2
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = firebaseToken });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Returns every non-null firebase_token across the user's active sessions.
    // Optionally filters out sessions whose timestamp is older than
    // `inactivityTtlSeconds` so callers don't push to stale devices. Mirrors
    // Python's db.get_user_session_firebase_tokens() — shipped now so a future
    // push plugin has a stable API to call.
    public async Task<List<string>> GetSessionFirebaseTokensAsync(
        string shortname, int? inactivityTtlSeconds = null, CancellationToken ct = default)
    {
        var result = new List<string>();
        await using var conn = await db.OpenAsync(ct);
        NpgsqlCommand cmd;
        if (inactivityTtlSeconds is int ttl && ttl > 0)
        {
            cmd = new NpgsqlCommand("""
                SELECT firebase_token FROM sessions
                WHERE shortname = $1
                  AND firebase_token IS NOT NULL
                  AND timestamp >= NOW() - ($2 || ' seconds')::interval
                """, conn);
            cmd.Parameters.Add(new() { Value = shortname });
            cmd.Parameters.Add(new() { Value = ttl.ToString() });
        }
        else
        {
            cmd = new NpgsqlCommand(
                "SELECT firebase_token FROM sessions WHERE shortname = $1 AND firebase_token IS NOT NULL",
                conn);
            cmd.Parameters.Add(new() { Value = shortname });
        }
        await using (cmd)
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
            }
        }
        return result;
    }

    public async Task<bool> IsSessionValidAsync(string token, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM sessions WHERE token = $1", conn);
        cmd.Parameters.Add(new() { Value = token });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // Atomic session activity check + touch. When SessionInactivityTtl > 0:
    //   * UPDATE bumps the session's timestamp to NOW() iff it exists AND is
    //     not older than `inactivityTtlSeconds`. Returns 1 row on success.
    //   * If the UPDATE affected 0 rows, the session is either missing OR
    //     stale — we then DELETE any stale row so the caller can't continue
    //     under an expired token.
    // Returns true if the session is live (and was just touched), false if
    // it was missing or evicted. Called from the JwtBearer OnTokenValidated
    // hook so every authenticated request resets the inactivity clock.
    public async Task<bool> TouchSessionAsync(string token, int inactivityTtlSeconds, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE sessions SET timestamp = NOW()
            WHERE token = $1
              AND timestamp >= NOW() - ($2 || ' seconds')::interval
            """, conn);
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = inactivityTtlSeconds.ToString() });
        var touched = await cmd.ExecuteNonQueryAsync(ct);
        if (touched > 0) return true;
        // Not touched — evict any stale row so SELECTs see the session gone.
        await using var purge = new NpgsqlCommand("DELETE FROM sessions WHERE token = $1", conn);
        purge.Parameters.Add(new() { Value = token });
        await purge.ExecuteNonQueryAsync(ct);
        return false;
    }

    public async Task DeleteSessionAsync(string token, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM sessions WHERE token = $1", conn);
        cmd.Parameters.Add(new() { Value = token });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ----- query support (used by QueryService for management/users) -----

    public Task<List<User>> QueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectAllColumns, q, Hydrate, ct, tableName: "users");

    public Task<int> CountQueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "users", q, ct);

    public async Task DeleteAllSessionsAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM sessions WHERE shortname = $1", conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Keep only the `keep` newest sessions for a user, evicting the rest.
    // Used to enforce max_sessions_per_user before creating a new session.
    public async Task EvictExcessSessionsAsync(string shortname, int keep, CancellationToken ct = default)
    {
        if (keep < 0) keep = 0;
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM sessions WHERE shortname = $1
            AND uuid NOT IN (
                SELECT uuid FROM sessions WHERE shortname = $1
                ORDER BY timestamp DESC LIMIT $2
            )
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = keep });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddJsonb(NpgsqlCommand cmd, string? json)
    {
        cmd.Parameters.Add(new()
        {
            Value = (object?)json ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
    }

    private static void AddJsonbNotNull(NpgsqlCommand cmd, string json)
    {
        cmd.Parameters.Add(new()
        {
            Value = json,
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
    }

    private static User Hydrate(NpgsqlDataReader r)
    {
        return new User
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
            Payload = JsonbHelpers.FromPayload(r.IsDBNull(13) ? null : r.GetString(13)),
            LastChecksumHistory = r.IsDBNull(14) ? null : r.GetString(14),
            ResourceType = JsonbHelpers.ParseEnumMember<ResourceType>(r.GetString(15)),
            Password = r.IsDBNull(16) ? null : r.GetString(16),
            Roles = JsonbHelpers.FromListString(r.IsDBNull(17) ? null : r.GetString(17)) ?? new(),
            Groups = JsonbHelpers.FromListString(r.IsDBNull(18) ? null : r.GetString(18)) ?? new(),
            Acl = JsonbHelpers.FromAclList(r.IsDBNull(19) ? null : r.GetString(19)),
            Relationships = JsonbHelpers.FromRelationships(r.IsDBNull(20) ? null : r.GetString(20)),
            Type = JsonbHelpers.ParseEnumNameLower<UserType>(r.GetString(21)),
            Language = JsonbHelpers.ParseEnumNameLower<Language>(r.GetString(22)),
            Email = r.IsDBNull(23) ? null : r.GetString(23),
            Msisdn = r.IsDBNull(24) ? null : r.GetString(24),
            LockedToDevice = r.GetBoolean(25),
            IsEmailVerified = r.GetBoolean(26),
            IsMsisdnVerified = r.GetBoolean(27),
            ForcePasswordChange = r.GetBoolean(28),
            DeviceId = NullIfEmpty(r, 29),
            GoogleId = NullIfEmpty(r, 30),
            FacebookId = NullIfEmpty(r, 31),
            SocialAvatarUrl = NullIfEmpty(r, 32),
            AttemptCount = r.IsDBNull(33) ? null : r.GetInt32(33),
            LastLogin = JsonbHelpers.FromDictStringObject(r.IsDBNull(34) ? null : r.GetString(34)),
            Notes = r.IsDBNull(35) ? null : r.GetString(35),
            QueryPolicies = r.IsDBNull(36) ? new() : ((string[])r.GetValue(36)).ToList(),
        };
    }

    // Reads a string column, returning null for both DB NULL and empty strings.
    private static string? NullIfEmpty(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return null;
        var s = r.GetString(ordinal);
        return s.Length == 0 ? null : s;
    }
}
