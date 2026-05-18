using Xunit;

namespace Dmart.Tests.Unit.Services;

// Serializes any test class that mutates the HOME env var to redirect the
// `~/.dmart/...` operator-overlay path (LanguageLoader and
// ActivationTemplateLoader both read from there). xUnit runs distinct test
// collections in parallel by default — two classes flipping HOME at the
// same time race, and the loaders bake whichever value was visible at the
// moment the load happened into a long-lived field/static cache. Grouping
// them into one collection with DisableParallelization forces sequential
// execution.
//
// `InvitationServiceParityTests` joins this collection too even though it
// doesn't mutate HOME itself: its static loader fields are initialized
// lazily on first test-method access, and an overlay class still running
// in parallel would otherwise leak its tmp HOME into that static cache.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HomeOverlayCollection
{
    public const string Name = "dmart-home-overlay";
}
