using Xunit;

namespace Dmart.Tests.Integration;

// Marks a fact that needs a PostgreSQL connection configured
// (DMART_TEST_PG_CONN env var, or a config.env reachable via
// DotEnv.FindConfigFile). When no DB is available, xUnit skips the
// test with a clear reason instead of each test body returning early.
// Behavior is identical to the prior `if (!DmartFactory.HasPg) return;`
// guard — same condition, same no-op when unset — with the win being
// that skipped tests show up in the summary instead of as silent
// passes.
public sealed class FactIfPgAttribute : FactAttribute
{
    public FactIfPgAttribute()
    {
        if (!DmartFactory.HasPg)
            Skip = "PostgreSQL not configured (set DMART_TEST_PG_CONN or create a config.env)";
    }
}

public sealed class TheoryIfPgAttribute : TheoryAttribute
{
    public TheoryIfPgAttribute()
    {
        if (!DmartFactory.HasPg)
            Skip = "PostgreSQL not configured (set DMART_TEST_PG_CONN or create a config.env)";
    }
}
