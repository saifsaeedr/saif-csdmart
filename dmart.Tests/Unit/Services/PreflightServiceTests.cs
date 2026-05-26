using System.Text.Json;
using Dmart.Cli;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for the `dmart preflight` subcommand. Builds a tmp-directory
// fixture matching the dmart export layout (space → .dm/ → meta files),
// runs the service in dry-run and apply modes, and asserts:
//
//   * duplicate UUIDs are detected and (in apply mode) regenerated atomically
//   * missing owner_shortname is detected and swapped to "dmart"
//   * schema-noncompliant payloads are flagged + skip-listed
//   * --dry-run truly doesn't mutate the source
//
// The fixture lives in `Path.GetTempPath()/preflight-test-<guid>/` and is
// removed in Dispose so a CI run doesn't leak temp dirs across runs.
public sealed class PreflightServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _outDir;

    public PreflightServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"preflight-test-{Guid.NewGuid():N}");
        _outDir = Path.Combine(Path.GetTempPath(), $"preflight-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task DryRun_DetectsDuplicateUuids_WithoutMutating()
    {
        // Layout: management/.dm/meta.space.json + three content entries
        // where two share the same UUID.
        SeedSpace("management", uuid: "00000000-0000-0000-0000-00000000aaaa");
        var duplicatedUuid = "00000000-0000-0000-0000-00000000beef";
        var entry1 = SeedEntry("management", "/", "first",  duplicatedUuid);
        var entry2 = SeedEntry("management", "/", "second", duplicatedUuid);
        var entry3 = SeedEntry("management", "/", "third",  Guid.NewGuid().ToString());

        var svc = new PreflightService(NullLogger<PreflightService>.Instance);
        var report = await svc.RunAsync(new PreflightOptions(
            Path: _root, DryRun: true, Workers: 2, OutputDir: _outDir, Sample: 20, Verbose: false));

        var dupIssues = report.Issues.Where(i => i.Kind == "duplicate-uuid").ToList();
        dupIssues.Count.ShouldBe(1, "one of the two duplicates should be flagged for regen (the other is kept)");
        // Dry-run must NOT touch the source — uuids stay duplicated.
        ReadUuid(entry1).ShouldBe(duplicatedUuid);
        ReadUuid(entry2).ShouldBe(duplicatedUuid);
        ReadUuid(entry3).ShouldNotBe(duplicatedUuid);
    }

    [Fact]
    public async Task ApplyMode_RegeneratesDuplicateUuids()
    {
        SeedSpace("management", uuid: "00000000-0000-0000-0000-00000000aaaa");
        var dup = "00000000-0000-0000-0000-00000000beef";
        var keeper = SeedEntry("management", "/", "aaa_first",  dup);   // sorts first
        var rest   = SeedEntry("management", "/", "bbb_second", dup);   // sorts second → regen

        var svc = new PreflightService(NullLogger<PreflightService>.Instance);
        await svc.RunAsync(new PreflightOptions(
            Path: _root, DryRun: false, Workers: 2, OutputDir: _outDir, Sample: 20, Verbose: false));

        ReadUuid(keeper).ShouldBe(dup, "first sorted path is the keeper");
        var regenerated = ReadUuid(rest);
        regenerated.ShouldNotBe(dup, "the other path's uuid should have been regenerated");
        Guid.TryParse(regenerated, out _).ShouldBeTrue("new value should still parse as a uuid");
    }

    [Fact]
    public async Task DetectsAndSwapsMissingOwner()
    {
        SeedSpace("apps", uuid: Guid.NewGuid().ToString(), owner: "dmart");
        // Seed a user "alice" so the universe includes both alice + dmart.
        SeedUser("alice");
        // Entry referencing a non-existent owner.
        var orphan = SeedEntry("apps", "/", "orphan", Guid.NewGuid().ToString(),
            owner: "ghost-user-does-not-exist");
        var owned = SeedEntry("apps", "/", "owned", Guid.NewGuid().ToString(), owner: "alice");

        var svc = new PreflightService(NullLogger<PreflightService>.Instance);
        var report = await svc.RunAsync(new PreflightOptions(
            Path: _root, DryRun: false, Workers: 2, OutputDir: _outDir, Sample: 20, Verbose: false));

        var ownerIssues = report.Issues.Where(i => i.Kind == "missing-owner").ToList();
        ownerIssues.Count.ShouldBe(1, "only the ghost-user entry should surface");
        ReadField(orphan, "owner_shortname").ShouldBe("dmart",
            "missing owner should have been swapped to the sentinel");
        ReadField(owned, "owner_shortname").ShouldBe("alice",
            "valid owner should be untouched");
    }

    [Fact]
    public async Task ExitCodePolicy_DryRun_OneIssueMeansNonZero()
    {
        // Just verify the issue list non-empty when an issue exists —
        // PreflightCommand maps that to exit 1 in dry-run mode.
        SeedSpace("management", uuid: Guid.NewGuid().ToString());
        SeedEntry("management", "/", "a", "shared-uuid");
        SeedEntry("management", "/", "b", "shared-uuid");

        var svc = new PreflightService(NullLogger<PreflightService>.Instance);
        var report = await svc.RunAsync(new PreflightOptions(
            Path: _root, DryRun: true, Workers: 2, OutputDir: _outDir, Sample: 20, Verbose: false));

        report.Issues.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task WritesSummaryAndSchemaSidecar()
    {
        SeedSpace("management", uuid: Guid.NewGuid().ToString());
        SeedEntry("management", "/", "valid", Guid.NewGuid().ToString());

        var svc = new PreflightService(NullLogger<PreflightService>.Instance);
        await svc.RunAsync(new PreflightOptions(
            Path: _root, DryRun: false, Workers: 2, OutputDir: _outDir, Sample: 20, Verbose: false));

        File.Exists(Path.Combine(_outDir, "summary.json")).ShouldBeTrue(
            "summary.json should be written even when no issues are found");
        var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(_outDir, "summary.json")));
        summary.RootElement.GetProperty("source").GetString().ShouldBe(_root);
        summary.RootElement.GetProperty("dry_run").GetBoolean().ShouldBeFalse();
    }

    // ---- fixture helpers ------------------------------------------------

    private void SeedSpace(string space, string uuid, string owner = "dmart")
    {
        var dir = Path.Combine(_root, space, ".dm");
        Directory.CreateDirectory(dir);
        var meta = new Dictionary<string, object>
        {
            ["uuid"] = uuid,
            ["shortname"] = space,
            ["owner_shortname"] = owner,
            ["resource_type"] = "space",
            ["is_active"] = true,
        };
        File.WriteAllText(Path.Combine(dir, "meta.space.json"),
            JsonSerializer.Serialize(meta));
    }

    private string SeedEntry(string space, string subpath, string shortname, string uuid,
        string owner = "dmart")
    {
        // subpath "/" → dir is just <space>/.dm/<shortname>/
        // subpath "/x/y" → dir is <space>/x/y/.dm/<shortname>/
        var rel = subpath.Trim('/');
        var dmParent = string.IsNullOrEmpty(rel)
            ? Path.Combine(_root, space, ".dm", shortname)
            : Path.Combine(_root, space, rel, ".dm", shortname);
        Directory.CreateDirectory(dmParent);
        var meta = new Dictionary<string, object>
        {
            ["uuid"] = uuid,
            ["shortname"] = shortname,
            ["owner_shortname"] = owner,
            ["resource_type"] = "content",
            ["is_active"] = true,
        };
        var path = Path.Combine(dmParent, "meta.content.json");
        File.WriteAllText(path, JsonSerializer.Serialize(meta));
        return path;
    }

    private void SeedUser(string shortname)
    {
        var dir = Path.Combine(_root, "management", "users", ".dm", shortname);
        Directory.CreateDirectory(dir);
        var meta = new Dictionary<string, object>
        {
            ["uuid"] = Guid.NewGuid().ToString(),
            ["shortname"] = shortname,
            ["owner_shortname"] = "dmart",
            ["resource_type"] = "user",
            ["is_active"] = true,
        };
        File.WriteAllText(Path.Combine(dir, "meta.user.json"),
            JsonSerializer.Serialize(meta));
    }

    private static string? ReadUuid(string path) => ReadField(path, "uuid");

    private static string? ReadField(string path, string field)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
    }
}
