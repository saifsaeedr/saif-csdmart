using System.IO;
using System.IO.Compression;
using Dmart.DataAdapters.Sql;
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

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "dmart.csproj")))
            d = d.Parent;
        d.ShouldNotBeNull();
        return d!.FullName;
    }
}
