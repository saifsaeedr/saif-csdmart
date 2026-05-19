using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
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

    // Exercises the --fast bulk path (fastUnsafeNoFkCheck:true). Without
    // this test the COPY-based code path is never executed by the suite,
    // and the riskiest piece of the optimization (binary column-order +
    // jsonb encoding + temp-table merge) ships untested. Asserts both
    // the row counts and a sampled jsonb field round-trip correctly.
    //
    // [FactIfFastImport] auto-skips when the DB role can't SET
    // session_replication_role (the documented hard-fail of --fast) so
    // CI under a non-superuser role doesn't fail the suite — the hard
    // failure is then the user's runtime concern, not the test suite's.
    [FactIfFastImport]
    public async Task Fast_Bulk_Import_RoundTrips_Entry_With_Jsonb_Fields()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();

        var spaceName = $"fast_bulk_{Guid.NewGuid():N}";
        const string entryName = "thing";
        const string description = "fast-bulk-test-description";

        var resp = await io.ImportZipAsync(
            BuildSingleEntryZip(spaceName, entryName, description),
            actor: null, preserveExisting: false, fastUnsafeNoFkCheck: true);

        resp.Status.ShouldBe(Status.Success);
        // Per-record failures would also surface as a non-empty list — fail
        // loud if the COPY path threw on any row.
        if (resp.Attributes?.GetValueOrDefault("failed") is List<Dictionary<string, object>> fails)
            fails.Count.ShouldBe(0, $"unexpected per-row failures: {string.Join(", ", fails.Select(f => f.GetValueOrDefault("error")))}");

        // The space + the single content entry both round-trip through the
        // import. Verify the entry came back and the jsonb description was
        // preserved verbatim (proves the jsonb COPY path).
        var entry = await entryRepo.GetAsync(spaceName, "/", entryName, ResourceType.Content);
        entry.ShouldNotBeNull("fast-bulk import did not land the content row");
        entry.Description?.En.ShouldBe(description, "jsonb description did not round-trip");
    }

    // Round 3 — exercises per-space parallelism with fastParallelism > 1.
    // Builds a zip carrying THREE distinct spaces each with its own
    // content entry; verifies each space's entry lands and the jsonb
    // descriptions round-trip. The riskiest piece is correct grouping —
    // entries leaking from one worker's space group into another's would
    // either (a) miss the target space entirely, or (b) cause a worker
    // to try to insert another space's entry under its own session
    // (which would fail the entry's space_name = ... lookup at re-read time).
    // Skipped when the DB role lacks session_replication_role privilege
    // — see Fast_Bulk_Import_RoundTrips_Entry_With_Jsonb_Fields above.
    [FactIfFastImport]
    public async Task Fast_Parallel_Imports_Multiple_Spaces()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();

        // Three independent spaces, each with one Content entry. The space
        // names are GUID-suffixed so this test is run-safe against any prior
        // test pollution.
        var stamp = Guid.NewGuid().ToString("N");
        var spaceA = $"fast_par_a_{stamp}";
        var spaceB = $"fast_par_b_{stamp}";
        var spaceC = $"fast_par_c_{stamp}";

        var ms = new MemoryStream();
        using (var ar = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (space, sn, desc) in new[] {
                (spaceA, "entry_a", "alpha-desc"),
                (spaceB, "entry_b", "beta-desc"),
                (spaceC, "entry_c", "gamma-desc"),
            })
            {
                var spaceMeta = new JsonObject
                {
                    ["uuid"] = Guid.NewGuid().ToString(),
                    ["shortname"] = space,
                    ["is_active"] = true,
                    ["owner_shortname"] = "dmart",
                    ["languages"] = new JsonArray("english"),
                };
                WriteEntry(ar, $"{space}/.dm/meta.space.json", spaceMeta.ToJsonString());

                var contentMeta = new JsonObject
                {
                    ["uuid"] = Guid.NewGuid().ToString(),
                    ["shortname"] = sn,
                    ["is_active"] = true,
                    ["owner_shortname"] = "dmart",
                    ["description"] = new JsonObject { ["en"] = desc },
                    ["payload"] = new JsonObject
                    {
                        ["content_type"] = "json",
                        ["body"] = new JsonObject(),
                    },
                };
                WriteEntry(ar, $"{space}/.dm/{sn}/meta.content.json", contentMeta.ToJsonString());
            }
        }
        ms.Position = 0;

        var resp = await io.ImportZipAsync(
            ms, actor: null, preserveExisting: false,
            fastUnsafeNoFkCheck: true, fastParallelism: 3);

        resp.Status.ShouldBe(Status.Success);
        if (resp.Attributes?.GetValueOrDefault("failed") is List<Dictionary<string, object>> fails)
            fails.Count.ShouldBe(0, $"unexpected per-row failures: {string.Join(", ", fails.Select(f => f.GetValueOrDefault("error")))}");

        // Verify every space's entry landed AND its description round-tripped.
        // If grouping leaked, one of these would come back null.
        var a = await entryRepo.GetAsync(spaceA, "/", "entry_a", ResourceType.Content);
        a.ShouldNotBeNull("space A entry missing");
        a.Description?.En.ShouldBe("alpha-desc");

        var b = await entryRepo.GetAsync(spaceB, "/", "entry_b", ResourceType.Content);
        b.ShouldNotBeNull("space B entry missing");
        b.Description?.En.ShouldBe("beta-desc");

        var c = await entryRepo.GetAsync(spaceC, "/", "entry_c", ResourceType.Content);
        c.ShouldNotBeNull("space C entry missing");
        c.Description?.En.ShouldBe("gamma-desc");
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
