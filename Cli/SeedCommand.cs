using Dmart.Models.Api;
using Dmart.Models.Enums;

namespace Dmart.Cli;

internal static class SeedCommand
{
    // Files half of `dmart seed`. Resolves the bundled spaces (embedded
    // manifest first, on-disk fallback) and copies them into spacesFolder.
    // Returns 0 on success, 1 on configuration error (no bundled seed).
    public static int SeedFiles(string spacesFolder, bool force)
    {
        // Two extract strategies, in order:
        //   1. Read from the embedded-files manifest XML inside the DLL.
        //      This is the standalone-binary path: the seed/ tree is baked
        //      in as EmbeddedResource and the build emits an authoritative
        //      path-to-resource-name index.
        //
        //      We parse the XML directly rather than going through
        //      ManifestEmbeddedFileProvider — that helper silently drops
        //      entries that share a basename with a sibling (e.g. the
        //      `schema.json` next to `schema/`), which corrupts a dmart
        //      space tree. Hand-walking the XML is faithful to what the
        //      manifest actually contains.
        //
        //   2. Filesystem fallback at {BaseDir}/seed/spaces — for builds
        //      that ship seed/ as loose files next to the binary instead
        //      of embedded.
        var spacesByName = new Dictionary<string, List<(string RelPath, Func<Stream> Open)>>(StringComparer.Ordinal);
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var manifestStream = asm.GetManifestResourceStream(
            "Microsoft.Extensions.FileProviders.Embedded.Manifest.xml");
        if (manifestStream is not null)
        {
            using (manifestStream)
            {
                var xdoc = System.Xml.Linq.XDocument.Load(manifestStream);
                var ns = xdoc.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;
                var fileSystem = xdoc.Root?.Element(ns + "FileSystem");
                var seedDir = fileSystem?
                    .Elements(ns + "Directory")
                    .FirstOrDefault(d => (string?)d.Attribute("Name") == "seed")?
                    .Elements(ns + "Directory")
                    .FirstOrDefault(d => (string?)d.Attribute("Name") == "spaces");
                if (seedDir is not null)
                {
                    foreach (var spaceEl in seedDir.Elements(ns + "Directory"))
                    {
                        var spaceName = (string?)spaceEl.Attribute("Name");
                        if (string.IsNullOrEmpty(spaceName)) continue;
                        var files = new List<(string, Func<Stream>)>();
                        CollectManifestFiles(spaceEl, ns, "", asm, files);
                        spacesByName[spaceName] = files;
                    }
                }
            }
        }

        if (spacesByName.Count == 0)
        {
            var fsPath = Path.Combine(AppContext.BaseDirectory, "seed", "spaces");
            if (Directory.Exists(fsPath))
            {
                foreach (var spaceDir in Directory.EnumerateDirectories(fsPath))
                {
                    var spaceName = Path.GetFileName(spaceDir);
                    var files = new List<(string, Func<Stream>)>();
                    foreach (var f in Directory.EnumerateFiles(spaceDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(spaceDir, f).Replace(Path.DirectorySeparatorChar, '/');
                        var captured = f;
                        files.Add((rel, () => File.OpenRead(captured)));
                    }
                    spacesByName[spaceName] = files;
                }
            }
        }

        if (spacesByName.Count == 0)
        {
            Console.Error.WriteLine(
                "Bundled seed not found. Rebuild with the seed/ tree present, or "
                + $"place it at {Path.Combine(AppContext.BaseDirectory, "seed", "spaces")}.");
            return 1;
        }

        Directory.CreateDirectory(spacesFolder);
        var totalCopied = 0;
        var totalSkipped = 0;
        foreach (var (spaceName, files) in spacesByName.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var dst = Path.Combine(spacesFolder, spaceName);
            Directory.CreateDirectory(dst);
            var copied = 0;
            var preExisting = 0;
            foreach (var (relPath, open) in files)
            {
                var target = Path.Combine(dst, relPath.Replace('/', Path.DirectorySeparatorChar));
                var exists = File.Exists(target);
                if (exists)
                {
                    preExisting++;
                    if (!force) continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var src = open();
                using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
                src.CopyTo(fs);
                copied++;
            }
            totalCopied += copied;
            totalSkipped += preExisting;
            var preLabel = force ? "overwritten" : "skipped";
            Console.WriteLine($"  {spaceName} \u2192 {dst}  (copied={copied}, {preLabel}={preExisting})");
        }
        Console.WriteLine(force
            ? $"Seeded {totalCopied} file(s) into {spacesFolder} ({totalSkipped} overwritten via --force)"
            : $"Seeded {totalCopied} file(s) into {spacesFolder} ({totalSkipped} already existed, left untouched)");
        return 0;
    }

    // DB half of `dmart seed`. Re-zips the on-disk spacesFolder in memory
    // and feeds it through ImportZipAsync.
    public static async Task<int> SeedDbAsync(
        string spacesFolder,
        string? dotenvPath,
        IDictionary<string, string?> dotenvValues,
        bool force)
    {
        if (!Directory.Exists(spacesFolder)
            || !Directory.EnumerateDirectories(spacesFolder).Any())
        {
            Console.Error.WriteLine(
                $"No spaces found at {spacesFolder}. Run `dmart seed files-only`"
                + " first or set SPACES_FOLDER in config.env.");
            return 1;
        }

        var (seedDbSettings, seedDb) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues);
        var importService = CliBootstrap.BuildImportExportService(seedDbSettings, seedDb);

        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
            zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var spaceDir in Directory.EnumerateDirectories(spacesFolder))
            {
                foreach (var file in Directory.EnumerateFiles(spaceDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(spacesFolder, file)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    var entry = archive.CreateEntry(rel);
                    await using var src = File.OpenRead(file);
                    await using var dst = entry.Open();
                    await src.CopyToAsync(dst);
                }
            }
        }
        zipStream.Position = 0;

        var resp = await importService.ImportZipAsync(zipStream, actor: null, preserveExisting: !force);
        if (resp.Status != Status.Success)
        {
            Console.Error.WriteLine($"Seed (db) failed: {resp.Error?.Message ?? "unknown error"}");
            return 1;
        }

        static int Read(Response r, string key)
            => r.Attributes is { } a && a.TryGetValue(key, out var v) && v is int i ? i : 0;

        var entries_inserted     = Read(resp, "entries_inserted");
        var attachments_inserted = Read(resp, "attachments_inserted");
        var spaces_inserted      = Read(resp, "spaces_inserted");
        var users_inserted       = Read(resp, "users_inserted");
        var roles_inserted       = Read(resp, "roles_inserted");
        var permissions_inserted = Read(resp, "permissions_inserted");
        var histories_inserted   = Read(resp, "histories_inserted");
        var skipped              = Read(resp, "skipped");
        var failed_count         = Read(resp, "failed_count");
        var totalInserted = entries_inserted + attachments_inserted + spaces_inserted
                          + users_inserted + roles_inserted + permissions_inserted + histories_inserted;

        Console.WriteLine($"Seeded {totalInserted} rows from {spacesFolder} (skipped {skipped} existing, {failed_count} failed)");
        Console.WriteLine($"  entries={entries_inserted} attachments={attachments_inserted} spaces={spaces_inserted}"
            + $" users={users_inserted} roles={roles_inserted} permissions={permissions_inserted}"
            + $" histories={histories_inserted}");

        if (failed_count > 0
            && resp.Attributes?.GetValueOrDefault("failed") is List<Dictionary<string, object>> failedList)
        {
            Console.Error.WriteLine($"Failures ({failedList.Count}):");
            foreach (var f in failedList)
            {
                var p = f.GetValueOrDefault("path") ?? "?";
                var k = f.GetValueOrDefault("kind") ?? "?";
                var e = f.GetValueOrDefault("error") ?? "?";
                Console.Error.WriteLine($"  [{k}] {p}: {e}");
            }
        }
        return failed_count > 0 ? 2 : 0;
    }

    // Walks the embedded-files manifest XML rooted at `dir` and appends every
    // descendant File entry to `outList` as (relativePath, streamFactory).
    // The streamFactory captures the assembly + ResourcePath so the caller
    // can defer opening until extraction time. Hand-walking the XML avoids
    // ManifestEmbeddedFileProvider's silent drops on basename-collision
    // siblings (e.g. `schema.json` next to `schema/`).
    //
    // Path-traversal defense: the manifest is generated at build time from
    // the in-repo seed/ tree, so a malicious "Name" attribute requires
    // either a compromised build artifact OR a future change that opens up
    // seed authoring to outside contributors. Belt-and-suspenders, we skip
    // any Name containing "..", "/", or "\\" — those are never valid
    // single-segment manifest names, and the rejection prevents
    // SeedFiles from writing outside the per-space target dir.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Trimming", "IL2026",
        Justification = "Resource names are read from the embedded manifest XML at runtime and passed straight to GetManifestResourceStream; no reflection on type metadata.")]
    private static void CollectManifestFiles(
        System.Xml.Linq.XElement dir,
        System.Xml.Linq.XNamespace ns,
        string relPrefix,
        System.Reflection.Assembly asm,
        List<(string RelPath, Func<Stream> Open)> outList)
    {
        static bool LooksUnsafe(string name) =>
            name == ".." || name.Contains('/') || name.Contains('\\');

        foreach (var f in dir.Elements(ns + "File"))
        {
            var name = (string?)f.Attribute("Name");
            var resPath = (string?)f.Element(ns + "ResourcePath");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(resPath)) continue;
            if (LooksUnsafe(name))
            {
                Console.Error.WriteLine($"[seed] skipping manifest entry with unsafe name: {name}");
                continue;
            }
            var rel = string.IsNullOrEmpty(relPrefix) ? name : relPrefix + "/" + name;
            var captured = resPath;
            outList.Add((rel, () => asm.GetManifestResourceStream(captured)
                ?? throw new InvalidOperationException($"Embedded resource missing: {captured}")));
        }
        foreach (var sub in dir.Elements(ns + "Directory"))
        {
            var name = (string?)sub.Attribute("Name");
            if (string.IsNullOrEmpty(name)) continue;
            if (LooksUnsafe(name))
            {
                Console.Error.WriteLine($"[seed] skipping manifest directory with unsafe name: {name}");
                continue;
            }
            var subPrefix = string.IsNullOrEmpty(relPrefix) ? name : relPrefix + "/" + name;
            CollectManifestFiles(sub, ns, subPrefix, asm, outList);
        }
    }
}
