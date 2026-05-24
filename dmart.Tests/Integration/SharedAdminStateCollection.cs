using Xunit;

namespace Dmart.Tests.Integration;

// Serializes test classes that touch the shared admin row — both
// mutators (attempt_count increments via wrong-password logins,
// ResetAttemptsAsync, or direct UPDATE) AND readers (any class whose
// tests call `_factory.AdminShortname/AdminPassword` to log in).
//
// xUnit parallelizes across test classes by default. Before this
// collection covered the readers, an admin-mutating test (e.g.
// UserAuthDbTests.Wrong_Password_Increments_AttemptCount) could push
// attempt_count past the lockout threshold concurrently with another
// class's LoginClient call, surfacing as a flaky 401 on the reader.
// The "Login_Returns_Records_Not_Attributes" and McpEndpointTests
// failures we saw on PR #65 and #66 were both this race.
//
// Joining this collection forces every admin-row toucher to run
// sequentially. Adds ~7% serialization to the integration suite (~50
// tests of ~630), in exchange for eliminating the auth-cluster flake.
// Sibling of AnonymousWorldCollection for the anonymous/world rows.
[CollectionDefinition(Name)]
public sealed class SharedAdminStateCollection
{
    public const string Name = "SharedAdminState";
}
