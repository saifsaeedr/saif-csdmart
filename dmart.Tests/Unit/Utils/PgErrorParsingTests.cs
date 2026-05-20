using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

// Pins PgErrorParsing.ExtractUndefinedColumn against PG's standard
// English-locale MessageText for SqlState 42703. The global exception
// handler in Program.cs uses this to name the offending field in the
// user-facing "unknown search field" error — drift here would silently
// degrade that message to the generic fallback.
public class PgErrorParsingTests
{
    [Theory]
    [InlineData("column \"asd\" does not exist", "asd")]
    [InlineData("column \"my_field_42\" does not exist", "my_field_42")]
    [InlineData("column \"with spaces\" does not exist", "with spaces")]
    // PG appends contextual lines after the headline message — HINT, LINE n,
    // a caret pointer. The regex must still find the column name in the
    // first line and not get confused by the rest of the body.
    [InlineData(
        "column \"asd\" does not exist\nLINE 1: SELECT asd FROM entries\n               ^",
        "asd")]
    public void ExtractUndefinedColumn_MatchesEnglishMessage(string message, string expected)
    {
        PgErrorParsing.ExtractUndefinedColumn(message).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relation \"foo\" does not exist")]   // wrong PG class (42P01, not 42703)
    [InlineData("syntax error at or near \"asd\"")]
    // Non-English locale: a server with lc_messages=es_ES.UTF-8 emits a
    // Spanish error that the regex can't match. Documented as a known
    // limitation — the caller falls back to a generic message in this case.
    [InlineData("no existe la columna «asd»")]
    public void ExtractUndefinedColumn_ReturnsNull_WhenNoMatch(string? message)
    {
        PgErrorParsing.ExtractUndefinedColumn(message).ShouldBeNull();
    }

    // ==================== ExtractUniqueViolation ====================
    //
    // PG's Detail line for SqlState 23505 is "Key (col)=(val) already exists."
    // The handler in Program.cs uses this to name the offending column in the
    // 409 response. LOWER(col) wrappers from expression-based unique indexes
    // are unwrapped so the user-visible column matches the field name they
    // typed (idx_users_email_lower_unique → "email", not "lower(email)").

    [Theory]
    [InlineData("Key (email)=(alice@example.com) already exists.", "email", "alice@example.com")]
    // LOWER(col) wrapper unwrap — matches case-insensitive unique indexes.
    [InlineData("Key (lower(email))=(alice@example.com) already exists.", "email", "alice@example.com")]
    [InlineData("Key (LOWER(email))=(alice@example.com) already exists.", "email", "alice@example.com")]
    // Empty value (NULL in the unique-violation context shouldn't ever
    // appear since unique indexes are WHERE NOT NULL, but pin the parse).
    [InlineData("Key (shortname)=() already exists.", "shortname", "")]
    // Composite key — the parser returns the whole "(col1, col2)" expression
    // as the column. Acceptable as-is for a diagnostic message; the caller
    // can fall back to a generic message if they want to hide compositeness.
    [InlineData("Key (uuid, space_name)=(abc, mgmt) already exists.", "uuid, space_name", "abc, mgmt")]
    public void ExtractUniqueViolation_ParsesDetail(string detail, string expectedKey, string expectedValue)
    {
        var (key, value) = PgErrorParsing.ExtractUniqueViolation(detail);
        key.ShouldBe(expectedKey);
        value.ShouldBe(expectedValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Some other PG detail format")]
    [InlineData("la llave (email)=(alice@example.com) ya existe.")]   // es_ES locale
    public void ExtractUniqueViolation_ReturnsNullPair_WhenNoMatch(string? detail)
    {
        var (key, value) = PgErrorParsing.ExtractUniqueViolation(detail);
        key.ShouldBeNull();
        value.ShouldBeNull();
    }

    // ==================== ExtractUniqueViolationKey ====================
    //
    // Fallback for when PG's Detail line is suppressed (terse log_error_verbosity,
    // some managed-PG vendors). Derives the offending field from the
    // constraint or index name following two conventions:
    //   - idx_<table>_<col>_unique   (this project's convention)
    //   - <table>_<col>_key          (PG's auto-generated name for inline UNIQUE)
    // The `_lower` suffix is stripped so idx_users_email_lower_unique → "email".

    [Theory]
    [InlineData("idx_users_email_lower_unique", "users", "email")]
    [InlineData("idx_users_msisdn_unique", "users", "msisdn")]
    [InlineData("idx_users_google_id_unique", "users", "google_id")]
    [InlineData("idx_users_apple_id_unique", "users", "apple_id")]
    // PG's auto-generated names for UNIQUE constraints — `<table>_<col>_key`.
    [InlineData("users_shortname_key", "users", "shortname")]
    [InlineData("entries_uuid_key", "entries", "uuid")]
    // When tableName is missing/empty the helper still strips the suffix
    // and returns whatever's between the prefix and the suffix — best-effort.
    [InlineData("idx_users_email_unique", "", "users_email")]
    public void ExtractUniqueViolationKey_StripsConventionalPrefixes(string constraint, string table, string expected)
    {
        PgErrorParsing.ExtractUniqueViolationKey(constraint, table).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "users")]
    [InlineData("", "users")]
    // Doesn't match either convention — helper returns null and the caller
    // falls back to a generic "resource already exists" message.
    [InlineData("some_random_index_name", "users")]
    [InlineData("idx_no_underscores_or_suffix", "users")]
    public void ExtractUniqueViolationKey_ReturnsNull_WhenNoMatch(string? constraint, string table)
    {
        PgErrorParsing.ExtractUniqueViolationKey(constraint, table).ShouldBeNull();
    }
}
