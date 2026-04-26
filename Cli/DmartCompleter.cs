using System.Text.Json;

namespace Dmart.Cli;

// Context-aware tab-completion for the REPL — mirrors Python cli.py's
// CustomCompleter. Completes commands, space names, and entry shortnames.
public sealed class DmartCompleter(DmartClient dmart) : IAutoCompleteHandler
{
    private static readonly string[] Commands =
    {
        "ls", "cd", "pwd", "switch", "mkdir", "create", "rm", "move",
        "cat", "print", "find", "whoami", "version", "help", "attach",
        "upload", "request", "progress", "import", "export", "exit", "quit",
    };

    public char[] Separators { get; set; } = { ' ' };

    public string[] GetSuggestions(string text, int index)
    {
        // If on first word, complete commands
        var trimmed = text.TrimStart();
        var spaceIdx = trimmed.IndexOf(' ');

        if (spaceIdx < 0)
        {
            // Completing the command itself
            return Commands.Where(c => c.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        var cmd = trimmed[..spaceIdx].ToLowerInvariant();
        var arg = trimmed[(spaceIdx + 1)..].TrimStart();

        return cmd switch
        {
            "s" or "switch" => CompleteSpaces(arg),
            "cd" => CompleteFolders(arg),
            _ => CompleteEntries(arg),
        };
    }

    private string[] CompleteSpaces(string prefix)
    {
        return dmart.SpaceNames
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private string[] CompleteFolders(string prefix)
    {
        return dmart.CurrentEntries
            .Where(e => e.GetProperty("resource_type").GetString() == "folder")
            .Select(e => e.GetProperty("shortname").GetString()!)
            .Where(sn => sn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private string[] CompleteEntries(string prefix)
    {
        return dmart.CurrentEntries
            .Select(e => e.GetProperty("shortname").GetString()!)
            .Where(sn => sn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
