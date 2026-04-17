using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Persists single-use invitation tokens.
//
// Row lifecycle: UpsertAsync on mint, GetValueAsync during login, DeleteAsync
// when the login consumes the invitation. The JWT on its own is not enough —
// invitation login requires a matching row here. That's what enforces
// "single-use": after DeleteAsync, subsequent replays of the same JWT fail
// even if the `expires` claim hasn't elapsed yet.
//
// `invitation_value` stores `{CHANNEL}:{identifier}` (e.g. "EMAIL:a@b.com",
// "SMS:+123") — Python-compatible, used by the login path to decide whether
// to mark email or msisdn as verified.
public sealed class InvitationRepository(Db db)
{
    public async Task UpsertAsync(string token, string value, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO invitations (uuid, invitation_token, invitation_value, timestamp)
            VALUES ($1, $2, $3, NOW())
            ON CONFLICT (invitation_token) DO UPDATE
                SET invitation_value = EXCLUDED.invitation_value, timestamp = NOW()
            """, conn);
        cmd.Parameters.Add(new() { Value = Guid.NewGuid() });
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetValueAsync(string token, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT invitation_value FROM invitations WHERE invitation_token = $1", conn);
        cmd.Parameters.Add(new() { Value = token });
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw as string;
    }

    public async Task DeleteAsync(string token, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM invitations WHERE invitation_token = $1", conn);
        cmd.Parameters.Add(new() { Value = token });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
