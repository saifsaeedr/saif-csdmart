using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Json;

// Lock in that every shipped seed/spaces/{space}/.dm/meta.space.json deserializes
// cleanly through the same path TryImportSpaceAsync uses, AND pin the
// STJ source-gen behavior that motivated the SpaceRepository `?? ""` backstops.
//
// The bug: `personal/.dm/meta.space.json` omits `root_registration_signature`
// and `icon`. The Space record declares both as non-nullable strings with a
// `= ""` initializer. With STJ source-gen + `required` members on the same
// record, deserialization routes through a parameterized-constructor path
// that does NOT run member initializers — so omitted fields land as null,
// not "". The DB columns are NOT NULL, so the upsert blew up until
// SpaceRepository.UpsertAsync added `?? ""` coercions.
//
// This test asserts that exact behavior: present-in-JSON → "", omitted → null.
// If STJ ever fixes the initializer skip (or someone "tidies" the seed JSON
// to fill in the missing fields), the assertions here will tell us we can
// drop the `?? ""` backstops in SpaceRepository.
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

        // Capture which optional string fields the JSON omits so we can assert
        // the STJ-source-gen-skips-initializer behavior below.
        var jsonHasRrs = node!.ContainsKey("root_registration_signature");
        var jsonHasPw  = node.ContainsKey("primary_website");
        var jsonHasIcon = node.ContainsKey("icon");

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

        // STJ source-gen + required members skips field initializers on the
        // parameterized-ctor path. Assert per-field: present-in-JSON keeps the
        // JSON value, omitted ends up null (NOT the C# default "").
        // SpaceRepository.UpsertAsync coerces null→"" before the SQL write.
        if (jsonHasRrs)
            space.RootRegistrationSignature.ShouldNotBeNull($"{spaceName}: rrs present in JSON should round-trip");
        else
            space.RootRegistrationSignature.ShouldBeNull(
                $"{spaceName}: STJ initializer-skip regression — RootRegistrationSignature was \"\" instead of null. "
                + "If STJ now runs initializers on the required-ctor path, the SpaceRepository `?? \"\"` backstop can be removed.");

        if (jsonHasPw)
            space.PrimaryWebsite.ShouldNotBeNull($"{spaceName}: primary_website present in JSON should round-trip");
        else
            space.PrimaryWebsite.ShouldBeNull(
                $"{spaceName}: STJ initializer-skip regression — PrimaryWebsite was \"\" instead of null.");

        if (jsonHasIcon)
            space.Icon.ShouldNotBeNull($"{spaceName}: icon present in JSON should round-trip");
        else
            space.Icon.ShouldBeNull(
                $"{spaceName}: STJ initializer-skip regression — Icon was \"\" instead of null.");
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
