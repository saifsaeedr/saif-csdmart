using Xunit;

namespace Dmart.Tests.Integration;

// Serializes test classes that mutate the shared admin user row
// (attempt_count increments via wrong-password logins,
// ResetAttemptsAsync, or direct UPDATE). xUnit parallelizes across
// test classes by default; before this collection existed, two such
// classes running concurrently could produce a locked admin row seen
// by any parallel admin-login helper elsewhere in the suite. Joining
// this collection forces the admin-mutators to run sequentially.
// Sibling of AnonymousWorldCollection for the anonymous/world rows.
[CollectionDefinition(Name)]
public sealed class SharedAdminStateCollection
{
    public const string Name = "SharedAdminState";
}
