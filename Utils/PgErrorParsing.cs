using System.Text.RegularExpressions;

namespace Dmart.Utils;

// Helpers for pulling structured fields out of PostgreSQL error messages
// when Npgsql doesn't already expose them. PostgresException populates
// ColumnName for column-tied errors (NOT NULL, FK, CHECK), but for
// undefined_column (SqlState 42703) the offending name only appears in the
// MessageText: `column "asd" does not exist`. The global PG exception
// handler in Program.cs reaches in here so the user-facing message can
// name the field the caller misspelled.
public static class PgErrorParsing
{
    // Matches PG's standard English MessageText for SqlState 42703. A server
    // running with a non-English `lc_messages` (e.g. es_ES.UTF-8 →
    // `no existe la columna «asd»`) won't match and the caller falls back
    // to a generic "Unknown search field" message — the error is still
    // surfaced cleanly, just without naming the field.
    private static readonly Regex UndefinedColumnRegex = new(
        @"column ""([^""]+)"" does not exist", RegexOptions.Compiled);

    public static string? ExtractUndefinedColumn(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = UndefinedColumnRegex.Match(message);
        return m.Success ? m.Groups[1].Value : null;
    }
}
