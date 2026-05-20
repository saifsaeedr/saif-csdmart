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

    // PG's Detail for SqlState 23505 (unique_violation) is
    //   `Key (<col_expr>)=(<value>) already exists.`
    // where <col_expr> may be a bare column ("email") or an indexed
    // expression ("lower(email)") for expression-based unique indexes.
    // We unwrap a `lower(<col>)` wrapper so callers see the user-visible
    // field name. Returns (null, null) if Detail doesn't match — non-English
    // locales emit a translated string and we just fall back to deriving the
    // column from the constraint name (see ExtractUniqueViolationKey).
    private static readonly Regex UniqueViolationDetailRegex = new(
        @"^Key \((?<col>.+)\)=\((?<val>.*)\) already exists\.?$",
        RegexOptions.Compiled);

    private static readonly Regex LowerWrapperRegex = new(
        @"^lower\((.+)\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (string? Key, string? Value) ExtractUniqueViolation(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return (null, null);
        var m = UniqueViolationDetailRegex.Match(detail);
        if (!m.Success) return (null, null);
        var col = m.Groups["col"].Value;
        var val = m.Groups["val"].Value;
        var lower = LowerWrapperRegex.Match(col);
        if (lower.Success) col = lower.Groups[1].Value;
        return (col, val);
    }

    // Strip the `<table>_` prefix and `_key`/`_unique` suffix from a unique
    // constraint or index name so callers can name the offending field even
    // when PG's Detail line is suppressed (server with
    // `log_error_verbosity=terse`, or a managed PG that strips Detail).
    // Recognized shapes:
    //   * `idx_<table>_<col>_unique`        — our convention in SqlSchema.cs
    //   * `<table>_<col>_key`               — PG's auto-generated name for
    //                                          inline UNIQUE / UNIQUE(...) constraints
    // `_lower` is stripped from the tail so `idx_users_email_lower_unique`
    // surfaces as `email` (matches the unwrap done on Detail).
    public static string? ExtractUniqueViolationKey(string? constraintName, string? tableName)
    {
        if (string.IsNullOrEmpty(constraintName)) return null;

        string? col = null;
        if (constraintName.StartsWith("idx_", StringComparison.Ordinal)
            && constraintName.EndsWith("_unique", StringComparison.Ordinal))
        {
            var inner = constraintName.Substring(4, constraintName.Length - 4 - "_unique".Length);
            if (!string.IsNullOrEmpty(tableName)
                && inner.StartsWith(tableName + "_", StringComparison.Ordinal))
            {
                inner = inner.Substring(tableName.Length + 1);
            }
            col = inner;
        }
        else if (constraintName.EndsWith("_key", StringComparison.Ordinal))
        {
            var inner = constraintName.Substring(0, constraintName.Length - "_key".Length);
            if (!string.IsNullOrEmpty(tableName)
                && inner.StartsWith(tableName + "_", StringComparison.Ordinal))
            {
                inner = inner.Substring(tableName.Length + 1);
            }
            col = inner;
        }

        if (string.IsNullOrEmpty(col)) return null;
        if (col.EndsWith("_lower", StringComparison.Ordinal))
            col = col.Substring(0, col.Length - "_lower".Length);
        return col;
    }
}
