using Spectre.Console;

namespace Dmart.Cli;

// Centralized color palette + small markup helpers. Before this every file
// reached into Spectre with ad-hoc literal colors (`[blue]`, `[green]`,
// `[bold yellow]`), so the same semantic concept (a path, an error, a
// success) showed up in three different colors across the file. Names
// reference roles, not colors, so a future palette swap stays in one file.
internal static class CliTheme
{
    // Toggled by --no-color or output redirection. When false, MarkupLine
    // becomes WriteLine and Wrap returns the input unchanged.
    public static bool ColorEnabled { get; set; } = true;

    // When true (set by --json), suppress all decorative output. Only
    // structured JSON / raw command results reach stdout.
    public static bool JsonOnly { get; set; } = false;

    // Roles
    public const string Path     = "aqua";       // space:/subpath references
    public const string Heading  = "yellow";     // labels like "Switched subpath to:"
    public const string Success  = "green";      // ok messages, current selection
    public const string Warning  = "yellow";     // recoverable issues
    public const string Error    = "red";        // failures
    public const string Muted    = "grey";       // hints, latency, etc.
    public const string Prompt   = "cornflowerblue";
    public const string Cmd      = "blue";       // command name in help

    // Wrap user-supplied text in `[role]…[/]`, escaping markup metacharacters
    // first so a path containing `[` or `]` can't crash the line.
    public static string Wrap(string role, string? text)
    {
        var safe = Markup.Escape(text ?? "");
        return ColorEnabled ? $"[{role}]{safe}[/]" : safe;
    }

    // For pre-built markup fragments — escape only the values, keep tags.
    public static string Escape(string? text) => Markup.Escape(text ?? "");

    // Print one Markup line, honoring JsonOnly + ColorEnabled.
    public static void Line(string markup)
    {
        if (JsonOnly) return;
        if (ColorEnabled) AnsiConsole.MarkupLine(markup);
        else AnsiConsole.WriteLine(StripMarkup(markup));
    }

    // Plain (already-formatted) text line.
    public static void Plain(string text)
    {
        if (JsonOnly) return;
        AnsiConsole.WriteLine(text);
    }

    // Strip [role]…[/] tags for the no-color path. Spectre's Markup parser
    // doesn't expose a public "render to plain" hook for AOT, so we do a
    // tiny manual pass. Handles Markup.Escape's escape sequences ([[ → [
    // and ]] → ]) so escaped user data like "ls [path]" comes out intact.
    private static string StripMarkup(string markup)
    {
        var sb = new System.Text.StringBuilder(markup.Length);
        var i = 0;
        while (i < markup.Length)
        {
            // Markup.Escape encodes literal '[' as '[[' and ']' as ']]'.
            // Decode those first so they don't get mistaken for tag bounds.
            if (i + 1 < markup.Length && markup[i] == '[' && markup[i + 1] == '[')
            { sb.Append('['); i += 2; continue; }
            if (i + 1 < markup.Length && markup[i] == ']' && markup[i + 1] == ']')
            { sb.Append(']'); i += 2; continue; }
            if (markup[i] == '[')
            {
                var close = markup.IndexOf(']', i);
                if (close > 0) { i = close + 1; continue; }
            }
            sb.Append(markup[i]);
            i++;
        }
        return sb.ToString();
    }
}
