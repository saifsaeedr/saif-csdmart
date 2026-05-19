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
}
