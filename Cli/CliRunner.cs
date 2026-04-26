using System.Text.RegularExpressions;
using Spectre.Console;

namespace Dmart.Cli;

// Entry point for the `dmart cli` subcommand. Mirrors the standalone dmart-cli
// binary — same REPL, command mode, and script mode.
public static class CliRunner
{
    private const int HistoryLimit = 1000;

    public static async Task<int> RunAsync(string[] args)
    {
        // Strip global flags before mode parsing — they apply to all modes.
        var (passthrough, strict) = ParseGlobalFlags(args);
        args = passthrough;

        // Auto-disable color when output is being piped (e.g. `dmart cli c "ls /" | jq`).
        if (Console.IsOutputRedirected) CliTheme.ColorEnabled = false;

        // Tell Spectre's renderer too — without this, AnsiConsole.Write(table)
        // would still emit ANSI escapes even when CliTheme.ColorEnabled is
        // false (Spectre makes its own decision based on its profile).
        if (!CliTheme.ColorEnabled)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        var settings = CliSettings.Load();

        PrintBanner(settings);

        using var dmart = new DmartClient(settings);
        var handler = new CommandHandler(dmart, settings) { StrictMode = strict };

        // Login
        var (ok, error) = await dmart.LoginAsync();
        if (!ok)
        {
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Login failed:")} {CliTheme.Escape(error ?? "unknown error")}");
            return 1;
        }
        CliTheme.Line($"  {CliTheme.Wrap(CliTheme.Muted, $"connected in {dmart.LastLoginLatencyMs} ms as")} " +
                      $"{CliTheme.Wrap(CliTheme.Heading, settings.Shortname)}");

        // Load spaces
        await dmart.FetchSpacesAsync();
        await dmart.ListAsync();
        PrintSpaces(dmart);

        // Determine mode
        var mode = "repl";
        var cmdArgs = args;

        if (args.Length >= 1)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "c" or "cmd":
                    mode = "cmd";
                    var offset = 1;
                    if (args.Length >= 2)
                    {
                        SwitchSpace(dmart, args[1]);
                        offset = 2;
                        if (args.Length >= 3 && args[2].StartsWith('/'))
                        {
                            dmart.CurrentSubpath = args[2];
                            offset = 3;
                        }
                    }
                    cmdArgs = args[offset..];
                    break;
                case "s" or "script":
                    mode = "script";
                    cmdArgs = args[1..];
                    break;
            }
        }

        var rc = 0;
        switch (mode)
        {
            case "cmd":
                await handler.ExecuteAsync(string.Join(' ', cmdArgs));
                rc = handler.LastCommandFailed ? 1 : 0;
                break;
            case "script":
                if (cmdArgs.Length < 1)
                {
                    CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Usage:")} dmart cli s <script_file>");
                    return 1;
                }
                rc = await RunScriptAsync(handler, cmdArgs[0]);
                break;
            default:
                CliTheme.Line($"Type {CliTheme.Wrap(CliTheme.Cmd, "?")} for help");
                await ReplAsync(dmart, handler);
                break;
        }

        CliTheme.Line(CliTheme.Wrap(CliTheme.Heading, "Good bye!"));
        return rc;
    }

    private static (string[] Args, bool Strict) ParseGlobalFlags(string[] args)
    {
        var strict = false;
        var keep = new List<string>(args.Length);
        foreach (var a in args)
        {
            switch (a)
            {
                case "--json":
                    CliTheme.JsonOnly = true;
                    CliTheme.ColorEnabled = false;
                    break;
                case "--no-color":
                    CliTheme.ColorEnabled = false;
                    break;
                case "--strict":
                    strict = true;
                    break;
                default:
                    keep.Add(a);
                    break;
            }
        }
        return (keep.ToArray(), strict);
    }

    private static void PrintBanner(CliSettings settings)
    {
        if (CliTheme.JsonOnly) return;
        var info = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev";
        // The InformationalVersion is space-separated "<describe> branch=<x> date=<y>".
        // For the banner we just want <describe>.
        var version = info.Split(' ', 2)[0];
        CliTheme.Line($"{CliTheme.Wrap(CliTheme.Heading, "DMART")} " +
                      $"{CliTheme.Wrap(CliTheme.Muted, "command line interface")} " +
                      $"{CliTheme.Wrap(CliTheme.Success, version)}");
        CliTheme.Line($"  {CliTheme.Wrap(CliTheme.Muted, "server")} " +
                      $"{CliTheme.Wrap(CliTheme.Path, settings.Url)} " +
                      $"{CliTheme.Wrap(CliTheme.Muted, "user")} " +
                      $"{CliTheme.Wrap(CliTheme.Heading, settings.Shortname)}");
    }

    private static async Task ReplAsync(DmartClient dmart, CommandHandler handler)
    {
        var historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dmart", "cli_history");
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);

        ReadLine.AutoCompletionHandler = new DmartCompleter(dmart);
        ReadLine.HistoryEnabled = true;
        if (File.Exists(historyPath))
        {
            // Symmetric load/save: use the same constant both ways. The old
            // code loaded 500 and saved 1000, silently truncating the persisted
            // history on the first run after a long session.
            foreach (var line in File.ReadAllLines(historyPath).TakeLast(HistoryLimit))
                ReadLine.AddHistory(line);
        }

        while (true)
        {
            try
            {
                var prompt = BuildPrompt(dmart);
                var input = ReadLine.Read(prompt);
                if (input is null) break;

                input = input.Trim();
                if (string.IsNullOrEmpty(input)) continue;
                if (input is "exit" or "q" or "quit") break;

                ReadLine.AddHistory(input);
                await handler.ExecuteAsync(input);
            }
            catch (Exception ex)
            {
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, ex.Message)}");
            }
        }

        try { await File.WriteAllLinesAsync(historyPath, ReadLine.GetHistory().TakeLast(HistoryLimit).ToArray()); }
        catch { /* ignore */ }
    }

    private static string BuildPrompt(DmartClient dmart)
    {
        // ReadLine.Read renders raw ANSI escapes, so we hand-build the prompt
        // string with explicit [..m codes rather than Spectre markup
        // (which only resolves through AnsiConsole). When color is off we
        // emit a plain prompt so piped/captured output stays clean.
        if (!CliTheme.ColorEnabled)
            return $"{dmart.CurrentSpace}:{dmart.CurrentSubpath} > ";
        // 33 = yellow (heading), 36 = cyan (path), 0 = reset.
        return $"[33m{dmart.CurrentSpace}[0m:[36m{dmart.CurrentSubpath}[0m > ";
    }

    private static async Task<int> RunScriptAsync(CommandHandler handler, string scriptPath)
    {
        if (!File.Exists(scriptPath))
        {
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Script not found:")} {CliTheme.Escape(scriptPath)}");
            return 1;
        }

        var variables = new Dictionary<string, string>();
        var inCommentBlock = false;
        var varRef = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}");

        foreach (var rawLine in await File.ReadAllLinesAsync(scriptPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("/*")) { inCommentBlock = true; continue; }
            if (line.StartsWith("*/")) { inCommentBlock = false; continue; }
            if (inCommentBlock) continue;
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0] == "VAR")
            {
                // VAR name value — value may itself reference earlier vars.
                var name = parts[1];
                var value = varRef.Replace(parts[2], m => variables.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
                variables[name] = value;
                continue;
            }

            // Substitute ${name} occurrences. Bare-name substitution (the
            // earlier `String.Replace(name, value)` form) is gone — it
            // collided with any other word containing the name as a substring
            // (e.g. a VAR named `id` would clobber `subpath_id`, `identity`).
            line = varRef.Replace(line, m => variables.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Success, "> ")}{CliTheme.Escape(line)}");
            await handler.ExecuteAsync(line);
            if (handler.StrictMode && handler.LastCommandFailed)
            {
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "--strict: aborting on command failure")}");
                return 1;
            }
        }
        return 0;
    }

    private static void SwitchSpace(DmartClient dmart, string prefix)
    {
        foreach (var name in dmart.SpaceNames)
        {
            if (name.StartsWith(prefix))
            {
                dmart.CurrentSpace = name;
                dmart.CurrentSubpath = "/";
                return;
            }
        }
    }

    private static void PrintSpaces(DmartClient dmart)
    {
        if (CliTheme.JsonOnly) return;
        var rendered = dmart.SpaceNames.Select(s =>
            s == dmart.CurrentSpace
                ? $"[bold {CliTheme.Heading}]{Markup.Escape(s)}[/]"
                : $"[{CliTheme.Cmd}]{Markup.Escape(s)}[/]");
        CliTheme.Line($"Available spaces: {string.Join("  ", rendered)}");
    }
}
