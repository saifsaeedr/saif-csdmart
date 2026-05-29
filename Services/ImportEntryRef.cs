using System.IO.Compression;

namespace Dmart.Services;

// Thin source-agnostic adapter for a single file inside an import dump.
// Replaces direct ZipArchiveEntry use throughout the import pipeline so the
// same passes can run over either a zip archive or a filesystem folder
// laid out like an export zip. FullName uses forward-slash semantics
// matching ZipArchiveEntry.FullName.
internal sealed class ImportEntryRef
{
    public string FullName { get; }
    public string Name { get; }
    // Absolute on-disk path for filesystem-sourced entries; null for zip
    // entries. Doubles as the source discriminator in the lean-walk path:
    // when non-null, related files (payload body, history.jsonl, attachment
    // body) are DERIVED from this path and opened directly, instead of being
    // pre-enumerated into lookup dictionaries.
    public string? AbsolutePath { get; }
    private readonly Func<Stream> _open;

    private ImportEntryRef(string fullName, Func<Stream> open, string? absolutePath)
    {
        FullName = fullName;
        var slash = fullName.LastIndexOf('/');
        Name = slash < 0 ? fullName : fullName[(slash + 1)..];
        _open = open;
        AbsolutePath = absolutePath;
    }

    public Stream Open() => _open();

    public static ImportEntryRef FromZip(ZipArchiveEntry ze)
        => new(ze.FullName, ze.Open, absolutePath: null);

    public static ImportEntryRef FromFile(string relativeName, string absolutePath)
        => new(relativeName, () => File.OpenRead(absolutePath), absolutePath);
}

// Source kind for an import run. Drives two behaviours that differ
// between a zip archive and a filesystem folder: the noun used in
// error messages (Describe), and whether the parallel-tail-passes
// path needs to prefetch bytes into memory before dispatching workers
// (zip needs it because ZipArchive isn't thread-safe; folder doesn't).
internal enum ImportSourceKind
{
    Zip,
    Filesystem,
}

internal static class ImportSourceKindExtensions
{
    // Noun threaded into error messages so the operator sees the wording
    // matching the source they invoked.
    public static string Describe(this ImportSourceKind k) => k switch
    {
        ImportSourceKind.Zip => "zip entry",
        ImportSourceKind.Filesystem => "file",
        _ => "entry",
    };
}
