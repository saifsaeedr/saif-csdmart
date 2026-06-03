namespace Dmart.DataAdapters.Sql;

internal static class ExpectedColumnPatcher
{
    // Columns the C# code expects to read/write on each table. Compared
    // against information_schema.columns during `dmart migrate`; any missing
    // columns get an ALTER TABLE ADD COLUMN issued dynamically.
    private static readonly Dictionary<string, (string Column, string Ddl)[]> ExpectedColumns = new()
    {
        ["users"] =
        [
            ("device_id", "TEXT"),
            ("google_id", "TEXT"),
            ("facebook_id", "TEXT"),
            ("apple_id", "TEXT"),
            ("social_avatar_url", "TEXT"),
            ("attempt_count", "INTEGER"),
            ("last_login", "JSONB"),
            ("notes", "TEXT"),
            ("locked_to_device", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        ],
        ["roles"] =
        [
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        ],
        ["permissions"] =
        [
            ("last_checksum_history", "TEXT"),
            ("allowed_roles", "JSONB"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        ],
        ["entries"] =
        [
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        ],
        ["spaces"] =
        [
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
            ("active_plugins", "JSONB"),
            ("hide_folders", "JSONB"),
            ("hide_space", "BOOLEAN"),
            ("ordinal", "INTEGER"),
            ("mirrors", "JSONB"),
        ],
        ["sessions"] =
        [
            ("firebase_token", "TEXT"),
        ],
    };

    // Compares each table's live columns to ExpectedColumns and issues
    // ALTER TABLE ADD COLUMN for any missing entries. Returns the count of
    // ALTERs actually issued. Skips tables that don't exist.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "Audited: table/column/ddl are iterated from a hardcoded schema map; no external input enters the SQL string.")]
    public static async Task<int> ApplyAsync(Npgsql.NpgsqlConnection conn, bool quiet)
    {
        var applied = 0;
        foreach (var (table, cols) in ExpectedColumns)
        {
            await using (var check = new Npgsql.NpgsqlCommand(
                "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = $1", conn))
            {
                check.Parameters.Add(new() { Value = table });
                if (await check.ExecuteScalarAsync() is null) continue;
            }

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var q = new Npgsql.NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = $1", conn))
            {
                q.Parameters.Add(new() { Value = table });
                await using var r = await q.ExecuteReaderAsync();
                while (await r.ReadAsync()) existing.Add(r.GetString(0));
            }

            foreach (var (column, ddl) in cols)
            {
                if (existing.Contains(column)) continue;
                var sql = $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {ddl}";
                await using var alter = new Npgsql.NpgsqlCommand(sql, conn);
                await alter.ExecuteNonQueryAsync();
                applied++;
                if (!quiet) Console.WriteLine($"  + {table}.{column} ({ddl})");
            }
        }
        return applied;
    }
}
