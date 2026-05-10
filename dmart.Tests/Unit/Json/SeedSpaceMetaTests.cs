using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Json;

// Lock in that every shipped seed/spaces/{space}/.dm/meta.space.json deserializes
// cleanly through the same path TryImportSpaceAsync uses. Caught a real bug:
// `personal/.dm/meta.space.json` was failing to land via `dmart seed` because
// (TBD — this test pinpoints which field).
public sealed class SeedSpaceMetaTests
{
    [Theory]
    [InlineData("management")]
    [InlineData("applications")]
    [InlineData("personal")]
    public void Seed_SpaceMeta_Deserializes(string spaceName)
    {
        var path = Path.Combine(
            FindRepoRoot(), "seed", "spaces", spaceName, ".dm", "meta.space.json");
        File.Exists(path).ShouldBeTrue($"missing {path}");

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json)?.AsObject();
        node.ShouldNotBeNull();

        // Mirrors TryImportSpaceAsync's pre-deserialize fix-up.
        node["space_name"] = spaceName;
        node["shortname"] ??= spaceName;
        node["subpath"] = "/";
        if (string.IsNullOrEmpty(node["owner_shortname"]?.GetValue<string>()))
            node["owner_shortname"] = "dmart";

        Space? space = null;
        System.Exception? ex = null;
        try { space = node.Deserialize(DmartJsonContext.Default.Space); }
        catch (System.Exception e) { ex = e; }
        ex.ShouldBeNull($"deserialize threw for {spaceName}: {ex?.Message}");
        space.ShouldNotBeNull();
        space!.Shortname.ShouldBe(spaceName);
        // NOTE: when the JSON omits non-required string fields with a `= ""`
        // initializer (RootRegistrationSignature, PrimaryWebsite, Icon),
        // STJ source-gen ends up with the property as null instead of "".
        // This is because the presence of `required` members on the record
        // routes deserialization through a parameterized-constructor path
        // that doesn't run member initializers. SpaceRepository.UpsertAsync
        // backstops with `?? ""` so the DB writes don't blow up, but a
        // regression test would belong in SeedImportTests (which exercises
        // the full upsert path).
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "dmart.csproj")))
            d = d.Parent;
        d.ShouldNotBeNull();
        return d!.FullName;
    }
}
