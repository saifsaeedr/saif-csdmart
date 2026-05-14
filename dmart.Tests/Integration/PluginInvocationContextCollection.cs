using Xunit;

namespace Dmart.Tests.Integration;

// Serializes any test class that mutates `PluginInvocationContext.CurrentShortname`
// or `CurrentActor`. Those fields are `[ThreadStatic]` (see
// Plugins/Native/PluginInvocationContext.cs), so xUnit's default parallel
// class scheduler can interleave reads/writes when two classes touching them
// run on the same scheduling slot. Joining this collection forces sequential
// class execution. Test classes opt in via
// `[Collection(PluginInvocationContextCollection.Name)]`.
[CollectionDefinition(Name)]
public sealed class PluginInvocationContextCollection
{
    public const string Name = "PluginInvocationContext";
}
