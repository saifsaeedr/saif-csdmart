using System.Diagnostics;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// True end-to-end test of the `dmart seed files-only` CLI command, including
// the embedded-files manifest XML walker that the existing SeedImportTests
// does not exercise (those build the zip from the on-disk seed/ tree, which
// matches only the filesystem-fallback path in SeedFiles).
//
// Skips when the published dmart binary is missing — the test runner does
// not force a publish, so a clean checkout that has only run `dotnet test`
// will skip this without false failures. It does NOT require Postgres.
//
// Three things this asserts:
//   1. The published binary's embedded resource manifest is well-formed and
//      the seed/ tree round-trips through the XML walker.
//   2. Idempotent re-run reports zero copies (skip path).
//   3. --force re-copies every file (overwrite path).
public sealed class SeedCliE2ETests
{
    [Fact]
    public void Seed_FilesOnly_Extracts_Embedded_Manifest_To_TempDir()
    {
        var binary = FindDmartBinary();
        if (binary is null)
        {
            // Skip via Assert.True with explanatory message — xUnit doesn't
            // surface a built-in dynamic-skip from a [Fact] without third-
            // party traits, and this branch fires only on a clean checkout
            // that hasn't built the dmart binary yet.
            Assert.True(true, "dmart binary not built — run `dotnet build -c Release` first");
            return;
        }

        var spacesFolder = Path.Combine(Path.GetTempPath(),
            $"dmart-seed-e2e-{Guid.NewGuid():N}", "spaces");
        try
        {
            // First pass: every file should be copied from the embedded manifest.
            var first = RunSeed(binary, spacesFolder, force: false);
            first.ExitCode.ShouldBe(0,
                $"first seed run failed — stdout:\n{first.Stdout}\nstderr:\n{first.Stderr}");
            first.Stdout.ShouldContain("Seeded ");
            first.Stdout.ShouldContain("(0 already existed");
            // Each shipped space should land — match the directory names
            // listed in the per-space line.
            foreach (var space in new[] { "applications", "management", "personal" })
            {
                var dst = Path.Combine(spacesFolder, space);
                Directory.Exists(dst).ShouldBeTrue($"missing {dst} after seed");
                Directory.EnumerateFileSystemEntries(dst).Any().ShouldBeTrue(
                    $"{dst} is empty — embedded manifest didn't extract any files");
            }

            // The stricter assertion: the basename-collision sibling pair
            // (`schema.json` next to `schema/`) must both exist. This is the
            // exact case that ManifestEmbeddedFileProvider drops silently —
            // it's the reason for the hand-rolled XML walker.
            var schemaJson = Path.Combine(spacesFolder, "applications", "schema.json");
            var schemaDir  = Path.Combine(spacesFolder, "applications", "schema");
            File.Exists(schemaJson).ShouldBeTrue(
                "schema.json missing — manifest walker dropped the basename-collision sibling");
            Directory.Exists(schemaDir).ShouldBeTrue(
                "schema/ dir missing — manifest walker dropped the basename-collision sibling");

            // Second pass without --force: idempotent skip, zero copies.
            var second = RunSeed(binary, spacesFolder, force: false);
            second.ExitCode.ShouldBe(0);
            second.Stdout.ShouldContain("Seeded 0 file(s)");
            second.Stdout.ShouldContain("left untouched");

            // Third pass with --force: every file re-copied as overwrite.
            var third = RunSeed(binary, spacesFolder, force: true);
            third.ExitCode.ShouldBe(0);
            third.Stdout.ShouldContain("overwritten via --force");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(spacesFolder)!, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Seed_Rejects_Unknown_Mode()
    {
        var binary = FindDmartBinary();
        if (binary is null)
        {
            Assert.True(true, "dmart binary not built — skipping");
            return;
        }

        var psi = new ProcessStartInfo(binary, "seed bogus-mode")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Point SpacesFolder somewhere harmless so we don't touch a real ~/.dmart.
        psi.Environment["DMART__SPACESFOLDER"] = Path.Combine(
            Path.GetTempPath(), $"dmart-seed-reject-{Guid.NewGuid():N}");
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(20_000).ShouldBeTrue("dmart did not exit within 20s");
        var stderr = proc.StandardError.ReadToEnd();
        proc.ExitCode.ShouldBe(1);
        stderr.ShouldContain("Unknown seed mode");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunSeed(
        string binary, string spacesFolder, bool force)
    {
        var args = force ? "seed files-only --force" : "seed files-only";
        var psi = new ProcessStartInfo(binary, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Override SpacesFolder for the child — DmartSettings binds from the
        // "Dmart" config section, so the env-var key is Dmart__SpacesFolder.
        psi.Environment["DMART__SPACESFOLDER"] = spacesFolder;
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        return (proc.ExitCode, stdout, stderr);
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
