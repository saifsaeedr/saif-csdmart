namespace Dmart.Cli;

// Single chokepoint for writing ~/.dmart/cli.ini (and any other cli.ini
// the operator chooses to maintain). Enforces 0600 perms on Unix so the
// file holding the dmart CLI password isn't readable by other users on
// the host — matches the ssh/gpg convention for private credentials.
// No-op on Windows where the security model is ACL-based; the file
// inherits the parent directory's ACL, which for a user-profile path
// is already restricted to the owner.
internal static class CliIniWriter
{
    public static void WriteSecure(string path, IEnumerable<string> lines)
    {
        File.WriteAllLines(path, lines);
        Chmod0600(path);
    }

    public static void WriteSecure(string path, string contents)
    {
        File.WriteAllText(path, contents);
        Chmod0600(path);
    }

    private static void Chmod0600(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
