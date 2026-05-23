using System.Reflection;
using Dmart.SqlAdapter.Helpers;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.SqlAdapter;

// Contract test: QueryService.HasSearchMetachar and
// SearchExpressionParser.IsSafeForAlternationValue must agree, because they
// encode the same grammar contract from opposite sides ("HasMetachar = true"
// means "IsSafe = false").
//
// They live in different assemblies — dmart.csproj excludes Dmart.SqlAdapter
// from compilation (AOT-unsafe SDK) — so the two implementations must be
// kept in sync by convention. This test makes the drift loud: feed both
// the same representative inputs and assert every result agrees.
//
// QueryService.HasSearchMetachar is private; the test reaches it via
// reflection. That's the price of the AOT-isolation boundary.
public class MetacharDriftTests
{
    private static readonly MethodInfo HasSearchMetachar =
        typeof(Dmart.Services.QueryService)
            .GetMethod("HasSearchMetachar", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "QueryService.HasSearchMetachar not found — has it been renamed " +
                "or made public? Update this test to match the new accessor and " +
                "verify the grammar contract still holds.");

    private static bool QsHasMetachar(string s) =>
        (bool)HasSearchMetachar.Invoke(null, new object[] { s })!;

    // Representative inputs: each token the parser is known to treat
    // specially, plus a handful of plain-text controls. New parser tokens
    // SHOULD be added here AND in both implementations. If only one side
    // is updated, the matching assertion below fails loudly.
    public static IEnumerable<object[]> ParserTokens => new[]
    {
        new object[] { "|", true },   // alternation
        new object[] { ":", true },   // field separator
        new object[] { "*", true },   // wildcard / existence
        new object[] { "(", true },
        new object[] { ")", true },
        new object[] { "[", true },
        new object[] { "]", true },
        new object[] { "{", true },
        new object[] { "}", true },
        new object[] { "\"", true },  // string delim (not honored today, plausible)
        new object[] { "'", true },
        new object[] { "\\", true },  // escape
        new object[] { "@", true },   // field marker
        new object[] { "<", true },   // comparison
        new object[] { ">", true },
        new object[] { "=", true },
        new object[] { "!", true },
        new object[] { " ", true },   // whitespace terminates token
        new object[] { "\t", true },
        new object[] { "alice", false },
        new object[] { "alice_123", false },
        new object[] { "alice-bob", false },
        new object[] { "alice.bob", false },
        new object[] { "", false },   // empty: nothing special inside
    };

    [Theory]
    [MemberData(nameof(ParserTokens))]
    public void Both_Implementations_Agree_On_Representative_Inputs(string input, bool expectMetachar)
    {
        var qsSaysMetachar = QsHasMetachar(input);
        var parserSaysSafe = SearchExpressionParser.IsSafeForAlternationValue(input);

        // The two sides are negations of each other by definition.
        qsSaysMetachar.ShouldBe(!parserSaysSafe,
            $"drift detected for input {Display(input)}: " +
            $"QueryService.HasSearchMetachar={qsSaysMetachar} but " +
            $"SearchExpressionParser.IsSafeForAlternationValue={parserSaysSafe} — " +
            $"one side has been updated without the other.");
        qsSaysMetachar.ShouldBe(expectMetachar,
            $"input {Display(input)}: expected HasSearchMetachar={expectMetachar} " +
            $"but got {qsSaysMetachar}.");
    }

    private static string Display(string s) => s switch
    {
        " " => "<space>",
        "\t" => "<tab>",
        ""  => "<empty>",
        _   => $"'{s}'",
    };
}
