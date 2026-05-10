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

        // Build a minimal, isolated zip: one space + one Content entry at root
        // subpath. Content under root is the simplest layout DecodeEntryPath
        // accepts — `{space}/.dm/{shortname}/meta.content.json` parses as
        // (subpath="/", shortname=`{shortname}`). A unique space name per
        // run keeps this independent of the shipped seed/ tree and of any
        // concurrent test that might be touching `applications` etc.
        var spaceName = $"force_upsert_{Guid.NewGuid():N}";
        const string entryName = "thing";
        const string originalDesc = "original-description";
        const string updatedDesc = "updated-description";

        await io.ImportZipAsync(
            BuildSingleEntryZip(spaceName, entryName, originalDesc),
            actor: null, preserveExisting: true);

        // Sanity: the row landed.
        var afterFirst = await entryRepo.GetAsync(spaceName, "/", entryName, ResourceType.Content);
        afterFirst.ShouldNotBeNull("first import did not land the content row");
        afterFirst.Description?.En.ShouldBe(originalDesc);

        // Re-import same content with a CHANGED description, preserveExisting:true.
        // Skip path: the existing row must NOT change.
        await io.ImportZipAsync(
            BuildSingleEntryZip(spaceName, entryName, updatedDesc),
            actor: null, preserveExisting: true);
        var afterSkip = await entryRepo.GetAsync(spaceName, "/", entryName, ResourceType.Content);
        afterSkip.ShouldNotBeNull();
        afterSkip.Description?.En.ShouldBe(originalDesc,
            "preserveExisting:true must NOT overwrite the existing description");

        // Re-import with preserveExisting:false (the --force path).
        // Upsert must replace the description.
        await io.ImportZipAsync(
            BuildSingleEntryZip(spaceName, entryName, updatedDesc),
            actor: null, preserveExisting: false);
        var afterForce = await entryRepo.GetAsync(spaceName, "/", entryName, ResourceType.Content);
        afterForce.ShouldNotBeNull();
        afterForce.Description?.En.ShouldBe(updatedDesc,
            "preserveExisting:false must overwrite the existing description");
    }

    // Builds a self-contained zip with one space + one Content entry directly
    // under root subpath. Description is the only mutable field we vary
    // across imports — it round-trips through the JSON columns without
    // needing schema validation.
    //
    // Using JsonObject rather than raw-string interpolation: the meta
    // contains nested `{}` literals (empty payload body) that confuse $$
    // raw-string brace matching, and a typed builder is easier to mutate
    // safely if more fields are added later.
    private static MemoryStream BuildSingleEntryZip(string spaceName, string entryName, string description)
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

            // Content meta at root subpath: `{space}/.dm/{name}/meta.content.json`
            // → DecodeEntryPath returns (subpath="/", shortname=`{name}`).
            var contentMeta = new JsonObject
            {
                ["uuid"] = Guid.NewGuid().ToString(),
                ["shortname"] = entryName,
                ["is_active"] = true,
                ["owner_shortname"] = "dmart",
                ["description"] = new JsonObject { ["en"] = description },
                ["payload"] = new JsonObject
                {
                    ["content_type"] = "json",
                    ["body"] = new JsonObject(),
                },
            };
            WriteEntry(ar, $"{spaceName}/.dm/{entryName}/meta.content.json",
                contentMeta.ToJsonString());
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
