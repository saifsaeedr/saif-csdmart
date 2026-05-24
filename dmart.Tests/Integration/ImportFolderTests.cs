using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Coverage for the folder-source variant of /managed/import (PR #61).
// Mirrors the ImportExportRoundTripTests harness but unpacks the export
// zip to disk and runs ImportFolderAsync against the directory tree —
// exercises the source-agnostic ImportFromEntriesAsync pipeline through
// the filesystem code path. Also pins behaviours the review called out:
// `.git/` and dotfile skipping, the meta.space.json sanity check, and the
// CLI pre-validation when --type disagrees with the target's shape.
public class ImportFolderTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ImportFolderTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Folder_Import_Round_Trips_From_Unzipped_Export()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();

        var spaceName = "iexfs_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var folder = new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "items", SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Folder, IsActive = true,
            OwnerShortname = "dmart", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var c1 = MakeContent(spaceName, "/items", "i1", new { sku = "F-1", price = 7 });
        var c2 = MakeContent(spaceName, "/items", "i2", new { sku = "F-2", price = 11 });
        foreach (var e in new[] { folder, c1, c2 })
            await entryRepo.UpsertAsync(e);

        // Export → unzip to disk so we can drive the folder importer.
        var q = new Query
        {
            Type = QueryType.Search, SpaceName = spaceName, Subpath = "/",
            FilterSchemaNames = new(), Limit = 10_000, RetrieveJsonPayload = true,
        };
        await using var exported = await io.ExportAsync(q, actor: null);
        using var ms = new MemoryStream();
        await exported.CopyToAsync(ms);
        ms.Position = 0;

        var stagingDir = Path.Combine(Path.GetTempPath(), $"dmart-folder-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            using (var read = new ZipArchive(ms, ZipArchiveMode.Read))
                read.ExtractToDirectory(stagingDir);

            // Delete the seeded rows so the import has actual work to do.
            foreach (var e in new[] { folder, c1, c2 })
                await entryRepo.DeleteAsync(e.SpaceName, e.Subpath, e.Shortname, e.ResourceType);

            var resp = await io.ImportFolderAsync(stagingDir, actor: null);
            resp.Status.ShouldBe(Status.Success);
            ((int)resp.Attributes!["entries_inserted"]!).ShouldBeGreaterThanOrEqualTo(3);

            // Verify a content entry round-tripped through the filesystem path,
            // including its externalized JSON payload body.
            var i1 = await entryRepo.GetAsync(spaceName, "/items", "i1", ResourceType.Content);
            i1.ShouldNotBeNull();
            i1!.Payload.ShouldNotBeNull();
            i1.Payload!.Body!.Value.GetProperty("sku").GetString().ShouldBe("F-1");

            var items = await entryRepo.GetAsync(spaceName, "/", "items", ResourceType.Folder);
            items.ShouldNotBeNull();
            items!.ResourceType.ShouldBe(ResourceType.Folder);
        }
        finally
        {
            foreach (var e in new[] { folder, c1, c2 })
                try { await entryRepo.DeleteAsync(e.SpaceName, e.Subpath, e.Shortname, e.ResourceType); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    [FactIfPg]
    public async Task Folder_Import_Hard_Fails_When_No_Space_Layout()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();

        // Build a directory that's NOT a dmart dump — it has subdirs and
        // files, but no subdir carries `.dm/meta.space.json`. ImportFolderAsync
        // should hard-fail before touching the DB.
        var tmp = Path.Combine(Path.GetTempPath(), $"dmart-folder-bogus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tmp, "looks_like_a_space"));
        await File.WriteAllTextAsync(Path.Combine(tmp, "looks_like_a_space", "hello.txt"), "not a real meta");
        try
        {
            var resp = await io.ImportFolderAsync(tmp, actor: null);
            resp.Status.ShouldBe(Status.Failed);
            resp.Error!.Message.ShouldContain("does not look like a dmart space dump");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [FactIfPg]
    public async Task Folder_Import_Skips_DotPrefixed_Directories()
    {
        // Stage a minimal valid dump with extra `.git/` and `.DS_Store`
        // siblings. The dot-prefix filter must drop them before the layout
        // validator sees them — otherwise `.git/HEAD` would either trip the
        // "not under a top-level space directory" check (confusing message)
        // or get parsed as if it were a dmart file.
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();

        var spaceName = "iexskip_" + Guid.NewGuid().ToString("N")[..6];
        var tmp = Path.Combine(Path.GetTempPath(), $"dmart-folder-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tmp, spaceName, ".dm"));
        Directory.CreateDirectory(Path.Combine(tmp, spaceName, ".git", "refs"));
        Directory.CreateDirectory(Path.Combine(tmp, ".DS_Store"));   // some tooling makes it a dir
        try
        {
            // Minimum valid meta.space.json so the layout sanity check passes.
            var spaceMeta = $$"""
                {
                  "uuid": "{{Guid.NewGuid()}}",
                  "shortname": "{{spaceName}}",
                  "space_name": "{{spaceName}}",
                  "subpath": "/",
                  "owner_shortname": "dmart",
                  "is_active": true,
                  "languages": ["en"],
                  "created_at": "2026-01-01T00:00:00Z",
                  "updated_at": "2026-01-01T00:00:00Z"
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(tmp, spaceName, ".dm", "meta.space.json"), spaceMeta);
            // Junk content that should be silently skipped:
            await File.WriteAllTextAsync(Path.Combine(tmp, spaceName, ".git", "HEAD"), "ref: refs/heads/main\n");
            await File.WriteAllTextAsync(Path.Combine(tmp, spaceName, ".git", "refs", "main"), "deadbeef\n");

            var resp = await io.ImportFolderAsync(tmp, actor: null);
            // Success means the dot-prefixed files were filtered out and
            // the layout validator only saw `{space}/.dm/meta.space.json`.
            // Failure with "not under a top-level space directory" would
            // mean the dot-prefix filter missed something.
            resp.Status.ShouldBe(Status.Success,
                customMessage: $"unexpected error: {resp.Error?.Message}");
        }
        finally
        {
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [FactIfFastImport]
    public async Task Folder_Import_With_BatchSize_One_Round_Trips()
    {
        // Pins the per-row batched flush path. With batchSize=1 the loop in
        // RunTailPassesAsync calls BulkInsertEntriesAsync after every Add,
        // exercising the path that would otherwise only run on imports with
        // millions of entries. If batching is correct, the final state must
        // match the no-batching (large-batchSize) round-trip exactly.
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();

        var spaceName = "iexbatch_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = spaceName, SpaceName = spaceName,
            Subpath = "/", OwnerShortname = "dmart", IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var folder = new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "batched", SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Folder, IsActive = true,
            OwnerShortname = "dmart", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        // Five content entries so batchSize=1 yields multiple bulk-COPY
        // flushes (the original code would have done one).
        var seeded = new List<Entry> { folder };
        for (var i = 0; i < 5; i++)
            seeded.Add(MakeContent(spaceName, "/batched", $"b{i}", new { idx = i }));
        foreach (var e in seeded)
            await entryRepo.UpsertAsync(e);

        var q = new Query
        {
            Type = QueryType.Search, SpaceName = spaceName, Subpath = "/",
            FilterSchemaNames = new(), Limit = 10_000, RetrieveJsonPayload = true,
        };
        await using var exported = await io.ExportAsync(q, actor: null);
        using var ms = new MemoryStream();
        await exported.CopyToAsync(ms);
        ms.Position = 0;

        var stagingDir = Path.Combine(Path.GetTempPath(), $"dmart-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            using (var read = new ZipArchive(ms, ZipArchiveMode.Read))
                read.ExtractToDirectory(stagingDir);

            foreach (var e in seeded)
                await entryRepo.DeleteAsync(e.SpaceName, e.Subpath, e.Shortname, e.ResourceType);

            // batchSize=1 with --fast forces a BulkInsertEntriesAsync call
            // after every entry, hammering the batched-flush path.
            var resp = await io.ImportFolderAsync(stagingDir, actor: null,
                preserveExisting: false, fastUnsafeNoFkCheck: true, fastParallelism: 1, batchSize: 1);
            resp.Status.ShouldBe(Status.Success,
                customMessage: $"unexpected error: {resp.Error?.Message}");

            for (var i = 0; i < 5; i++)
            {
                var got = await entryRepo.GetAsync(spaceName, "/batched", $"b{i}", ResourceType.Content);
                got.ShouldNotBeNull($"entry b{i} missing after batched import");
                got!.Payload!.Body!.Value.GetProperty("idx").GetInt32().ShouldBe(i);
            }
        }
        finally
        {
            foreach (var e in seeded)
                try { await entryRepo.DeleteAsync(e.SpaceName, e.Subpath, e.Shortname, e.ResourceType); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    [FactIfPg]
    public async Task Folder_Import_Remap_Drops_Content_Under_Target_Space_And_Subpath()
    {
        // Remap mode: source folder contains content (no meta.space.json
        // at the top); --space and --subpath together drop it under an
        // existing space's subpath. Verifies the prefix-prepend path
        // through the layout validator and the DB.
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();

        var spaceName = "iexremap_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = spaceName, SpaceName = spaceName,
            Subpath = "/", OwnerShortname = "dmart", IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        // Stage a partial export: just a single content entry's meta and
        // its externalized JSON payload body. The source has NO
        // meta.space.json at the root — that's the contract for remap mode.
        var src = Path.Combine(Path.GetTempPath(), $"dmart-remap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(src, ".dm", "gizmo"));
        try
        {
            var contentMeta = $$"""
                {
                  "uuid": "{{Guid.NewGuid()}}",
                  "shortname": "gizmo",
                  "is_active": true,
                  "owner_shortname": "dmart",
                  "created_at": "2026-01-01T00:00:00Z",
                  "updated_at": "2026-01-01T00:00:00Z",
                  "payload": {
                    "content_type": "json",
                    "schema_shortname": null,
                    "body": "gizmo.json"
                  }
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(src, ".dm", "gizmo", "meta.content.json"), contentMeta);
            await File.WriteAllTextAsync(Path.Combine(src, "gizmo.json"), """{"sku": "REMAPPED"}""");

            var resp = await io.ImportFolderAsync(src, actor: null,
                preserveExisting: false, fastUnsafeNoFkCheck: false, fastParallelism: 1,
                batchSize: ImportExportService.DefaultBatchSize,
                targetSpace: spaceName, targetSubpath: "/products");
            resp.Status.ShouldBe(Status.Success,
                customMessage: $"unexpected error: {resp.Error?.Message}");

            // Verify the entry landed under {spaceName}:/products with the
            // remapped subpath (not at the source's top level).
            var gizmo = await entryRepo.GetAsync(spaceName, "/products", "gizmo", ResourceType.Content);
            gizmo.ShouldNotBeNull("entry missing — remap should have placed it at /products/gizmo");
            gizmo!.Payload!.Body!.Value.GetProperty("sku").GetString().ShouldBe("REMAPPED");
        }
        finally
        {
            try { await entryRepo.DeleteAsync(spaceName, "/products", "gizmo", ResourceType.Content); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
            try { Directory.Delete(src, recursive: true); } catch { }
        }
    }

    [FactIfPg]
    public async Task Folder_Import_Remap_Hard_Fails_On_Missing_Target_Space()
    {
        // Pre-flight check: the target space must already exist. If the
        // operator typos the space name, fail before touching anything.
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();

        var src = Path.Combine(Path.GetTempPath(), $"dmart-remap-miss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        try
        {
            var resp = await io.ImportFolderAsync(src, actor: null,
                preserveExisting: false, fastUnsafeNoFkCheck: false, fastParallelism: 1,
                batchSize: ImportExportService.DefaultBatchSize,
                targetSpace: "no-such-space-" + Guid.NewGuid().ToString("N")[..6],
                targetSubpath: "/anywhere");
            resp.Status.ShouldBe(Status.Failed);
            resp.Error!.Message.ShouldContain("does not exist");
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Cli_Import_TypeZip_On_Directory_Exits_Nonzero()
    {
        // Pre-validation in Program.cs: `--type=zip` against a directory
        // target should fail-fast with a clean error message, not let
        // File.OpenRead(dir) throw a raw IO exception further down.
        var binary = FindDmartBinary();
        if (binary is null)
        {
            Assert.True(true, "dmart binary not built — skipping");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"dmart-cli-typezip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // `--type=zip` is explicit, so the auto-detect skip-warning path
            // is bypassed; the type-vs-shape pre-validation should fire.
            var psi = new ProcessStartInfo(binary, $"import --type=zip {dir}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // Force stdin redirected so any interactive prompt is bypassed.
            psi.RedirectStandardInput = true;
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Close();
            proc.WaitForExit(20_000).ShouldBeTrue("dmart did not exit within 20s");
            var stderr = proc.StandardError.ReadToEnd();
            proc.ExitCode.ShouldBe(1);
            stderr.ShouldContain("--type=zip requires a regular file");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static Entry MakeContent(string space, string subpath, string shortname, object body)
    {
        var bodyJson = JsonSerializer.Serialize(body);
        return new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Content,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(bodyJson).RootElement.Clone(),
            },
        };
    }

    // Walks up from cwd to find dmart.csproj, then probes the conventional
    // build output paths. Returns null when none exist — caller should skip.
    private static string? FindDmartBinary()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "dmart.csproj")))
            d = d.Parent;
        if (d is null) return null;
        var candidates = new[]
        {
            Path.Combine(d.FullName, "bin", "Release", "net10.0", "dmart"),
            Path.Combine(d.FullName, "bin", "Debug",   "net10.0", "dmart"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
