using Xunit;

// Run the whole test assembly serially (no cross-collection parallelism).
//
// The integration suite spins up many WebApplicationFactory instances that all
// share ONE Postgres and process-global static plugin state (e.g. the static
// service provider behind NativePluginCallbacks, and [ThreadStatic]
// PluginInvocationContext). xUnit's default cross-collection parallelism races
// on those shared resources, producing intermittent, unrelated failures:
//   * --fast imports lose the race for the connection pool → a shard's open
//     throws → shard-failed → the import reports Failed;
//   * a plugin callback resolves a service provider a *parallel* factory has
//     already disposed → ObjectDisposedException.
// Both pass in isolation and flaked only under the full parallel suite.
//
// The codebase had been patching this one serializing [CollectionDefinition] at
// a time, but each new shared-state interaction surfaced a fresh flake. Disabling
// parallelization assembly-wide removes the entire class of race for a few
// minutes of extra wall-clock — the right trade for a deterministic suite given
// the shared DB + static plugin state. (The existing per-collection definitions
// are now redundant but harmless.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
