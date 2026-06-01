using Xunit;

namespace Dmart.Tests.Unit.Services;

// Serializes any test class that mutates the HOME env var to redirect the
// `~/.dmart/...` operator-overlay path (LanguageLoader reads from there).
// xUnit runs distinct test collections in parallel by default — two classes
// flipping HOME at the same time race, and the loader bakes whichever value
// was visible at the moment the load happened into a long-lived field.
// Grouping HOME-mutating classes into one collection with
// DisableParallelization forces sequential execution.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HomeOverlayCollection
{
    public const string Name = "dmart-home-overlay";
}
