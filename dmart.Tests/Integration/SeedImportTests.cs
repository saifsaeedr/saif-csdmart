using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Reproduces the user-reported bug "personal space does not exist in db
// after seed". Builds the same in-memory zip the `dmart seed db-only`
// command builds, runs ImportZipAsync, and asserts every shipped sample
// space landed in the spaces table — together with surfacing per-record
// failures so the fix can target the actual cause.
public class SeedImportTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SeedImportTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Seed_Imports_All_Three_Sample_Spaces_Including_Personal()
    {
        var sp = _factory.Services;
        _factory.CreateClient(); // boots AdminBootstrap → ensures `dmart` user exists
        var io = sp.GetRequiredService<ImportExportService>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();

        // Same zip-build as Program.cs `dmart seed db-only`.
        var seedRoot = Path.Combine(FindRepoRoot(), "seed", "spaces");
        Directory.Exists(seedRoot).ShouldBeTrue($"missing {seedRoot}");
        using var zipStream = new MemoryStream();
        using (var ar = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var spaceDir in Directory.EnumerateDirectories(seedRoot))
            {
                foreach (var f in Directory.EnumerateFiles(spaceDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(seedRoot, f).Replace(Path.DirectorySeparatorChar, '/');
                    var entry = ar.CreateEntry(rel);
                    using var src = File.OpenRead(f);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
            }
        }
        zipStream.Position = 0;

        var resp = await io.ImportZipAsync(zipStream, actor: null, preserveExisting: true);

        // Dump per-record failures so the test output identifies the exact cause.
        if (resp.Attributes?.GetValueOrDefault("failed") is List<Dictionary<string, object>> fails && fails.Count > 0)
        {
            var lines = new List<string> { $"Failures ({fails.Count}):" };
            foreach (var f in fails)
                lines.Add($"  [{f.GetValueOrDefault("kind")}] {f.GetValueOrDefault("path")}: {f.GetValueOrDefault("error")}");
            // ShouldBe with a custom message embeds the dump in xunit output.
            fails.Count.ShouldBe(0, string.Join("\n", lines));
        }

        // Verify each shipped space landed.
        foreach (var name in new[] { "applications", "management", "personal" })
        {
            var space = await spaceRepo.GetAsync(name);
            space.ShouldNotBeNull($"space '{name}' missing from DB after seed");
        }
    }

    // --force upsert behavior: re-importing the same content with
    // preserveExisting:false should overwrite mutable fields, while
    // preserveExisting:true must leave them as-is. Pins the contract
    // documented for `dmart seed --force` (mirrors `dmart import -r`).
    [FactIfPg]
    public async Task Force_Upsert_Replaces_Mutable_Fields_While_Skip_Preserves_Them()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();

        // Build a minimal, isolated zip with one schema in a fresh space.
        // Using a unique space name keeps this test independent of the
        // shipped seed/ tree and any other concurrent test that might be
        // touching `applications` / `management` / `personal`.
        var spaceName = $"force_upsert_{Guid.NewGuid():N}";
        const string schemaName = "thing";
        const string originalDesc = "original-description";
        const string updatedDesc = "updated-description";

        await io.ImportZipAsync(
            BuildSingleSchemaZip(spaceName, schemaName, originalDesc),
            actor: null, preserveExisting: true);

        // Sanity: the row landed.
        var afterFirst = await entryRepo.GetAsync(spaceName, "/schema", schemaName, ResourceType.Schema);
        afterFirst.ShouldNotBeNull("first import did not land the schema row");
        afterFirst.Description?.En.ShouldBe(originalDesc);

        // Re-import same content with a CHANGED description, preserveExisting:true.
        // Skip path: the existing row must NOT change.
        await io.ImportZipAsync(
            BuildSingleSchemaZip(spaceName, schemaName, updatedDesc),
            actor: null, preserveExisting: true);
        var afterSkip = await entryRepo.GetAsync(spaceName, "/schema", schemaName, ResourceType.Schema);
        afterSkip.ShouldNotBeNull();
        afterSkip.Description?.En.ShouldBe(originalDesc,
            "preserveExisting:true must NOT overwrite the existing description");

        // Re-import with preserveExisting:false (the --force path).
        // Upsert must replace the description.
        await io.ImportZipAsync(
            BuildSingleSchemaZip(spaceName, schemaName, updatedDesc),
            actor: null, preserveExisting: false);
        var afterForce = await entryRepo.GetAsync(spaceName, "/schema", schemaName, ResourceType.Schema);
        afterForce.ShouldNotBeNull();
        afterForce.Description?.En.ShouldBe(updatedDesc,
            "preserveExisting:false must overwrite the existing description");
    }

    // Builds a self-contained zip with one space + one schema entry. Mirrors
    // the on-disk shape `dmart export` produces and `dmart seed`/`dmart import`
    // consume. Description is the only mutable field we vary across imports
    // — it round-trips cleanly through the JSON columns without needing a
    // schema-validation cycle.
    //
    // Using JsonObject rather than raw-string interpolation: the schema meta
    // contains nested `{}` literals (empty payload body) that confuse $$
    // raw-string brace matching, and a typed builder is easier to mutate
    // safely if more fields are added later.
    private static MemoryStream BuildSingleSchemaZip(string spaceName, string schemaName, string description)
    {
        var ms = new MemoryStream();
        using (var ar = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Space meta — minimum non-null required fields.
            var spaceMeta = new JsonObject
            {
                ["uuid"] = Guid.NewGuid().ToString(),
                ["shortname"] = spaceName,
                ["is_active"] = true,
                ["owner_shortname"] = "dmart",
                ["languages"] = new JsonArray("english"),
            };
            WriteEntry(ar, $"{spaceName}/.dm/meta.space.json", spaceMeta.ToJsonString());

            // Schema meta with a description we can mutate to assert upsert.
            // Empty payload object — the importer accepts schemas with no
            // body file (round-trip parity with `dmart export`).
            var schemaMeta = new JsonObject
            {
                ["uuid"] = Guid.NewGuid().ToString(),
                ["shortname"] = schemaName,
                ["is_active"] = true,
                ["owner_shortname"] = "dmart",
                ["description"] = new JsonObject { ["en"] = description },
                ["payload"] = new JsonObject
                {
                    ["content_type"] = "json",
                    ["body"] = new JsonObject(),
                },
            };
            WriteEntry(ar, $"{spaceName}/.dm/schema/{schemaName}/meta.schema.json",
                schemaMeta.ToJsonString());
        }
        ms.Position = 0;
        return ms;
    }

    private static void WriteEntry(ZipArchive ar, string path, string json)
    {
        var entry = ar.CreateEntry(path);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(json);
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
