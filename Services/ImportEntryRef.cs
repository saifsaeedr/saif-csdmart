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
    private readonly Func<Stream> _open;

    private ImportEntryRef(string fullName, Func<Stream> open)
    {
        FullName = fullName;
        var slash = fullName.LastIndexOf('/');
        Name = slash < 0 ? fullName : fullName[(slash + 1)..];
        _open = open;
    }

    public Stream Open() => _open();

    public static ImportEntryRef FromZip(ZipArchiveEntry ze)
        => new(ze.FullName, ze.Open);

    public static ImportEntryRef FromFile(string relativeName, string absolutePath)
        => new(relativeName, () => File.OpenRead(absolutePath));
}
