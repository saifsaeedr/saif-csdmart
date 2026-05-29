namespace Dmart.Services;

// Plain-text work-list sidecar for `dmart import`: one source-relative meta
// path per line — the same `rel` the filesystem lean walk computes
// (e.g. "people/distributor/.dm/meta.folder.json").
//
// Two purposes:
//   * an import persists its enumerated list once (so a re-run can `--from-list`
//     it and skip the potentially very expensive walk of a large/remote tree);
//   * `--from-list` feeds a list produced by a prior run OR by external tooling,
//     e.g. `find <src> -name 'meta.*.json' -printf '%P\n' > list.txt`.
//
// Plain text (not JSON) is deliberate: it composes directly with find/grep/awk
// and the admin_scripts preflight tooling. Write is atomic (.tmp + rename, the
// same pattern as ImportCheckpointStore) and streamed so a 100k+ entry list is
// never held as one giant string. Read skips blank lines and `#` comments.
// There is intentionally NO header or validation — the list is trusted as-is;
// the caller guarantees the source tree is frozen.
public static class ImportWorkList
{
    // Default sidecar next to the source folder — mirrors the "hidden file in
    // the source" shape of ImportCheckpointStore.DefaultPathFor.
    public static string DefaultPathFor(string sourceFolder)
        => Path.Combine(sourceFolder, ".dmart-import-worklist.txt");

    // Atomic, streamed write. `rels` are written verbatim, one per line.
    public static void Write(string path, IEnumerable<string> rels)
    {
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, append: false))
            foreach (var rel in rels)
                w.WriteLine(rel);
        File.Move(tmp, path, overwrite: true);
    }

    // Reads the list, trimming each line and skipping blanks and `#` comments.
    // Lets the file fail loudly (IOException) if it's missing/unreadable so the
    // CLI surfaces a clean error instead of silently importing nothing.
    public static List<string> Read(string path)
    {
        var rels = new List<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            rels.Add(line);
        }
        return rels;
    }

    // Best-effort delete (used to tidy the auto-written default on a clean run).
    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
