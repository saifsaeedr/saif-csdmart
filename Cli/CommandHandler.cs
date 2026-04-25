using System.Text.Json;
using Spectre.Console;

namespace Dmart.Cli;

// Mirrors Python cli.py's action() function — dispatches user commands.
public sealed class CommandHandler(DmartClient dmart, CliSettings settings)
{
    // True when the script driver wants the next non-2xx to abort the run.
    public bool StrictMode { get; set; }
    // True after a command saw a non-success response — surfaced to the script
    // driver so a `--strict` run can exit with the right code.
    public bool LastCommandFailed { get; private set; }

    private static readonly Dictionary<string, (string Usage, string Summary)> CommandHelp = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ls"]       = ("ls [path] [page]",                "List entries under current subpath."),
        ["cd"]       = ("cd <folder | .. | @space[/sub]>", "Enter folder; .. goes up; @space switches space."),
        ["pwd"]      = ("pwd",                             "Print current space:subpath."),
        ["switch"]   = ("switch <space>",                  "Switch to space (prefix match)."),
        ["mkdir"]    = ("mkdir <name>",                    "Create folder under current subpath."),
        ["create"]   = ("create <type|space> <name>",      "Create entry of <type>; or 'create space <name>'."),
        ["rm"]       = ("rm [--dry-run] [-f] <name|*>",    "Delete entry; --dry-run prints; -f skips confirm."),
        ["move"]     = ("move <type> <src> <dst>",         "Move resource."),
        ["mv"]       = ("mv <type> <src> <dst>",           "Alias for move."),
        ["print"]    = ("print <name>",                    "Print entry metadata (alias: p)."),
        ["cat"]      = ("cat <name | path/name | *>",      "Print entry data (alias: c)."),
        ["find"]     = ("find <pattern> [--type <rt>]",    "Search current space for pattern."),
        ["whoami"]   = ("whoami",                          "Show user, server, current space:subpath."),
        ["version"]  = ("version",                         "Show CLI build + server manifest."),
        ["attach"]   = ("attach <sn> <entry> <type> <file>", "Upload attachment."),
        ["upload"]   = ("upload schema <name> <file> | upload csv <type> <sub> <schema> <file>",
                                                            "Upload schema or CSV."),
        ["request"]  = ("request <json_file>",             "POST raw managed/request JSON."),
        ["progress"] = ("progress <sub> <sn> <action>",    "Progress a ticket."),
        ["import"]   = ("import <zip_file>",               "Import a ZIP archive."),
        ["export"]   = ("export <query_json>",             "Export to ZIP."),
        ["help"]     = ("help [command]",                  "Show this help, or details for one command."),
        ["exit"]     = ("exit | q | quit | Ctrl+D",        "Exit the CLI."),
    };

    public async Task ExecuteAsync(string input)
    {
        LastCommandFailed = false;
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var oldSpace = dmart.CurrentSpace;
        var oldSubpath = dmart.CurrentSubpath;

        switch (parts[0].ToLowerInvariant())
        {
            case "h" or "help" or "?":
                PrintHelp(parts.Length >= 2 ? parts[1] : null);
                break;

            case "pwd":
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Heading, dmart.CurrentSpace)}:{CliTheme.Wrap(CliTheme.Path, dmart.CurrentSubpath)}");
                break;

            case "whoami":
                PrintWhoami();
                break;

            case "version":
                await PrintVersionAsync();
                break;

            case "ls":
                await HandleLsAsync(parts);
                dmart.CurrentSpace = oldSpace;
                dmart.CurrentSubpath = oldSubpath;
                await dmart.ListAsync();
                break;

            case "cd":
                await HandleCdAsync(parts);
                break;

            case "s" or "switch":
                await HandleSwitchAsync(parts);
                break;

            case "mkdir" when parts.Length >= 2:
                PrintJson(await dmart.CreateFolderAsync(parts[1]));
                break;

            case "create" when parts.Length >= 3:
                if (parts[1] == "space")
                    PrintJson(await dmart.ManageSpaceAsync(parts[2], "create"));
                else
                    PrintJson(await dmart.CreateEntryAsync(parts[1], parts[2]));
                await dmart.ListAsync();
                break;

            case "rm" when parts.Length >= 2:
                await HandleRmAsync(parts);
                break;

            case "move" or "mv" when parts.Length >= 4:
                await HandleMoveAsync(parts);
                break;

            case "find" when parts.Length >= 2:
                await HandleFindAsync(parts);
                break;

            case "p" or "print" when parts.Length >= 2:
                await HandlePrintAsync(parts[1]);
                break;

            case "c" or "cat" when parts.Length >= 2:
                await HandleCatAsync(parts);
                break;

            case "attach" when parts.Length >= 5:
                PrintJson(await dmart.AttachAsync(parts[1], parts[2], parts[3], parts[4]));
                break;

            case "upload" when parts.Length >= 2:
                await HandleUploadAsync(parts);
                dmart.CurrentSpace = oldSpace;
                dmart.CurrentSubpath = oldSubpath;
                await dmart.ListAsync();
                break;

            case "request" when parts.Length >= 2:
                PrintJson(await dmart.RequestFromFileAsync(parts[1]));
                break;

            case "progress" when parts.Length >= 4:
                PrintJson(await dmart.ProgressTicketAsync(parts[1], parts[2], parts[3]));
                break;

            case "import" when parts.Length >= 2:
                PrintJson(await dmart.ImportZipAsync(parts[1]));
                break;

            case "export" when parts.Length >= 2:
                CliTheme.Plain(await dmart.ExportAsync(parts[1]));
                break;

            default:
                LastCommandFailed = true;
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Unknown command:")} {CliTheme.Escape(parts[0])}");
                var suggestion = SuggestCommand(parts[0]);
                if (suggestion is not null)
                    CliTheme.Line($"  did you mean {CliTheme.Wrap(CliTheme.Cmd, suggestion)}?");
                else
                    CliTheme.Line($"  type {CliTheme.Wrap(CliTheme.Cmd, "help")} for the command list");
                break;
        }
    }

    // ---- ls ----

    private async Task HandleLsAsync(string[] parts)
    {
        if (parts.Length >= 2 && !int.TryParse(parts[1], out _))
        {
            var target = parts[1].TrimStart('/').TrimEnd('/');
            if (target.StartsWith('@'))
            {
                var (space, subpath) = ParseSpaceRef(target);
                if (space is not null) await SwitchSpaceAsync(space);
                if (subpath is not null) dmart.CurrentSubpath = subpath;
            }
            else
            {
                dmart.CurrentSubpath = target;
            }
            await dmart.ListAsync();
        }

        CliTheme.Line($"{CliTheme.Wrap(CliTheme.Heading, dmart.CurrentSpace)}:{CliTheme.Wrap(CliTheme.Path, dmart.CurrentSubpath)}");

        if (CliTheme.JsonOnly)
        {
            // Machine-readable mode: emit the raw entries array verbatim.
            foreach (var e in dmart.CurrentEntries) Console.WriteLine(e);
            return;
        }

        if (dmart.CurrentEntries.Count == 0)
        {
            CliTheme.Line($"  {CliTheme.Wrap(CliTheme.Muted, "(empty)")}");
            return;
        }

        var page = (parts.Length >= 2 && int.TryParse(parts[^1], out var pg)) ? pg : settings.Pagination;

        // Render in pages of `page` rows. A single large page would force the
        // user to scroll; a single tiny page would fragment a small listing.
        var pageEntries = new List<JsonElement>(page);
        var idx = 0;
        var total = dmart.CurrentEntries.Count;
        foreach (var e in dmart.CurrentEntries)
        {
            pageEntries.Add(e);
            idx++;
            if (pageEntries.Count == page || idx == total)
            {
                RenderEntriesTable(pageEntries);
                pageEntries.Clear();
                if (idx < total)
                {
                    AnsiConsole.Write("q: quit, Enter: next page ");
                    var key = Console.ReadKey(true);
                    Console.WriteLine();
                    if (key.KeyChar == 'q') return;
                }
            }
        }
    }

    private static void RenderEntriesTable(List<JsonElement> entries)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[grey]#[/]").Alignment(Justify.Right));
        table.AddColumn("[grey]type[/]");
        table.AddColumn("[grey]shortname[/]");
        table.AddColumn("[grey]payload[/]");

        var i = 1;
        foreach (var entry in entries)
        {
            var sn = entry.GetProperty("shortname").GetString() ?? "";
            var rt = entry.GetProperty("resource_type").GetString() ?? "";
            var icon = rt == "folder" ? ":file_folder:" : ":page_facing_up:";
            var payloadInfo = "";
            if (entry.TryGetProperty("attributes", out var attrs) &&
                attrs.ValueKind == JsonValueKind.Object &&
                attrs.TryGetProperty("payload", out var payload) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("content_type", out var ct))
            {
                var schema = payload.TryGetProperty("schema_shortname", out var ss)
                    ? $" / {Markup.Escape(ss.GetString() ?? "")}" : "";
                payloadInfo = $"{Markup.Escape(ct.GetString() ?? "")}{schema}";
            }
            table.AddRow(
                $"[grey]{i}[/]",
                $"{icon} [grey]{Markup.Escape(rt)}[/]",
                $"[{CliTheme.Success}]{Markup.Escape(sn)}[/]",
                payloadInfo.Length > 0 ? $"[{CliTheme.Muted}]{payloadInfo}[/]" : "");
            i++;
        }
        AnsiConsole.Write(table);
    }

    // ---- cd ----

    private async Task HandleCdAsync(string[] parts)
    {
        if (parts.Length < 2)
        {
            dmart.CurrentSubpath = "/";
            await dmart.ListAsync();
            AnnounceSubpath();
            return;
        }

        var target = parts[1];

        if (target == "..")
        {
            if (dmart.CurrentSubpath != "/")
            {
                var idx = dmart.CurrentSubpath.LastIndexOf('/');
                dmart.CurrentSubpath = idx <= 0 ? "/" : dmart.CurrentSubpath[..idx];
            }
            await dmart.ListAsync();
            AnnounceSubpath();
            return;
        }

        if (target.StartsWith('@'))
        {
            var (space, subpath) = ParseSpaceRef(target);
            if (space is not null) await SwitchSpaceAsync(space);
            if (subpath is not null) dmart.CurrentSubpath = subpath;
            await dmart.ListAsync();
            AnnounceSubpath();
            return;
        }

        // Find matching folder
        foreach (var entry in dmart.CurrentEntries)
        {
            var sn = entry.GetProperty("shortname").GetString() ?? "";
            var rt = entry.GetProperty("resource_type").GetString() ?? "";
            if (rt == "folder" && sn.StartsWith(target))
            {
                dmart.CurrentSubpath = dmart.CurrentSubpath == "/"
                    ? sn : $"{dmart.CurrentSubpath}/{sn}";
                await dmart.ListAsync();
                AnnounceSubpath();
                return;
            }
        }
        LastCommandFailed = true;
        CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Folder not found:")} {CliTheme.Escape(target)}");
    }

    private void AnnounceSubpath()
        => CliTheme.Line($"{CliTheme.Wrap(CliTheme.Heading, "Switched subpath to:")} {CliTheme.Wrap(CliTheme.Success, dmart.CurrentSubpath)}");

    // ---- switch space ----

    private async Task HandleSwitchAsync(string[] parts)
    {
        if (parts.Length < 2)
        {
            PrintSpaces();
            return;
        }
        if (await SwitchSpaceAsync(parts[1]))
        {
            await dmart.ListAsync();
            PrintSpaces();
        }
        else
        {
            LastCommandFailed = true;
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Space not found:")} {CliTheme.Escape(parts[1])}");
        }
    }

    // ---- rm ----

    private async Task HandleRmAsync(string[] parts)
    {
        // Parse flags. The first non-flag arg is the target.
        var dryRun = false;
        var force = false;
        var args = new List<string>();
        foreach (var p in parts.Skip(1))
        {
            switch (p)
            {
                case "--dry-run": case "-n": dryRun = true; break;
                case "-f": case "--force": force = true; break;
                default: args.Add(p); break;
            }
        }
        if (args.Count == 0)
        {
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Usage:")} rm [--dry-run] [-f] <name|*>");
            LastCommandFailed = true;
            return;
        }

        if (args[0] == "*")
        {
            await dmart.ListAsync();
            var targets = dmart.CurrentEntries.ToList();
            if (dryRun)
            {
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Warning, $"Would delete {targets.Count} entries:")}");
                foreach (var e in targets)
                    CliTheme.Line($"  {CliTheme.Escape(e.GetProperty("shortname").GetString() ?? "")}");
                return;
            }
            if (!force && !ConfirmDestructive($"Delete ALL {targets.Count} entries under {dmart.CurrentSpace}:{dmart.CurrentSubpath}?"))
                return;
            foreach (var entry in targets)
            {
                var sn = entry.GetProperty("shortname").GetString()!;
                var rt = entry.GetProperty("resource_type").GetString()!;
                AnsiConsole.Write($"{Markup.Escape(sn)} ");
                PrintJson(await dmart.DeleteAsync(sn, rt));
            }
            return;
        }
        if (args[0] == "space" && args.Count >= 2)
        {
            if (dryRun)
            {
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Warning, "Would delete space:")} {CliTheme.Escape(args[1])}");
                return;
            }
            if (!force && !ConfirmDestructive($"Delete entire SPACE '{args[1]}'? This is not reversible."))
                return;
            PrintJson(await dmart.ManageSpaceAsync(args[1], "delete"));
            return;
        }
        // Find by shortname
        var target = args[0];
        foreach (var entry in dmart.CurrentEntries)
        {
            var sn = entry.GetProperty("shortname").GetString()!;
            var rt = entry.GetProperty("resource_type").GetString()!;
            if (sn == target)
            {
                if (dryRun)
                {
                    CliTheme.Line($"{CliTheme.Wrap(CliTheme.Warning, "Would delete:")} {CliTheme.Escape(sn)} ({CliTheme.Escape(rt)})");
                    return;
                }
                if (rt == "folder" && !force && !ConfirmDestructive($"Delete folder '{sn}' (may contain children)?"))
                    return;
                PrintJson(await dmart.DeleteAsync(sn, rt));
                await dmart.ListAsync();
                return;
            }
        }
        LastCommandFailed = true;
        CliTheme.Line($"{CliTheme.Wrap(CliTheme.Warning, "Item not found")}");
    }

    private bool ConfirmDestructive(string question)
    {
        // In script/JsonOnly mode there's no user to prompt — fail closed
        // unless the caller passed -f. (Caller checks `force` first; if we
        // got here, they didn't.)
        if (CliTheme.JsonOnly || Console.IsInputRedirected)
        {
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "refusing destructive op without -f in non-interactive mode")}");
            LastCommandFailed = true;
            return false;
        }
        AnsiConsole.Markup($"{CliTheme.Wrap(CliTheme.Warning, question)} [y/N] ");
        var line = Console.ReadLine();
        return string.Equals(line?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    // ---- move ----

    private async Task HandleMoveAsync(string[] parts)
    {
        var type = parts[1];
        var src = parts[2].StartsWith('/') ? parts[2] : $"{dmart.CurrentSubpath}/{parts[2]}";
        var dst = parts[3].StartsWith('/') ? parts[3] : $"{dmart.CurrentSubpath}/{parts[3]}";
        var srcPath = string.Join('/', src.Split('/')[..^1]);
        var srcName = src.Split('/')[^1];
        var dstPath = string.Join('/', dst.Split('/')[..^1]);
        var dstName = dst.Split('/')[^1];
        PrintJson(await dmart.MoveAsync(type, srcPath, srcName, dstPath, dstName));
    }

    // ---- print / cat ----

    private async Task HandlePrintAsync(string shortname)
    {
        foreach (var entry in dmart.CurrentEntries)
        {
            var sn = entry.GetProperty("shortname").GetString()!;
            var rt = entry.GetProperty("resource_type").GetString()!;
            if (sn.StartsWith(shortname))
            {
                PrintJson(await dmart.MetaAsync(rt, sn));
                return;
            }
        }
        LastCommandFailed = true;
        CliTheme.Line($"{CliTheme.Wrap(CliTheme.Warning, "Item not found")}");
    }

    private async Task HandleCatAsync(string[] parts)
    {
        var target = parts[1];
        if (target == "*")
        {
            foreach (var e in dmart.CurrentEntries)
                PrintJson(e);
            return;
        }
        // If target has /, navigate temporarily
        if (target.Contains('/'))
        {
            var oldSub = dmart.CurrentSubpath;
            var idx = target.LastIndexOf('/');
            dmart.CurrentSubpath = target[..idx];
            target = target[(idx + 1)..];
            await dmart.ListAsync();
            dmart.CurrentSubpath = oldSub;
        }
        foreach (var entry in dmart.CurrentEntries)
        {
            if (entry.GetProperty("shortname").GetString()!.StartsWith(target))
            {
                PrintJson(entry);
                return;
            }
        }
        LastCommandFailed = true;
        CliTheme.Line($"{CliTheme.Wrap(CliTheme.Warning, "Item not found")}");
    }

    // ---- find ----

    private async Task HandleFindAsync(string[] parts)
    {
        // find <pattern> [--type <rt>] [--limit N]
        string? type = null;
        var limit = 50;
        var posArgs = new List<string>();
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i] == "--type" && i + 1 < parts.Length) { type = parts[++i]; }
            else if (parts[i] == "--limit" && i + 1 < parts.Length && int.TryParse(parts[i + 1], out var l)) { limit = l; i++; }
            else posArgs.Add(parts[i]);
        }
        if (posArgs.Count == 0)
        {
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Usage:")} find <pattern> [--type <rt>] [--limit N]");
            return;
        }
        var pattern = string.Join(' ', posArgs);
        var resp = await Spinner($"Searching {dmart.CurrentSpace} for '{pattern}'…",
            () => dmart.FindAsync(pattern, dmart.CurrentSubpath == "/" ? "/" : dmart.CurrentSubpath, type, limit));

        if (CliTheme.JsonOnly) { PrintJson(resp); return; }

        if (!resp.TryGetProperty("records", out var recs) || recs.ValueKind != JsonValueKind.Array || recs.GetArrayLength() == 0)
        {
            CliTheme.Line($"  {CliTheme.Wrap(CliTheme.Muted, "(no matches)")}");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[grey]subpath[/]");
        table.AddColumn("[grey]type[/]");
        table.AddColumn("[grey]shortname[/]");
        foreach (var r in recs.EnumerateArray())
        {
            var sn  = r.TryGetProperty("shortname", out var snEl) ? snEl.GetString() ?? "" : "";
            var rt  = r.TryGetProperty("resource_type", out var rtEl) ? rtEl.GetString() ?? "" : "";
            var sub = r.TryGetProperty("subpath", out var spEl) ? spEl.GetString() ?? "" : "";
            table.AddRow(
                $"[{CliTheme.Path}]{Markup.Escape(sub)}[/]",
                $"[grey]{Markup.Escape(rt)}[/]",
                $"[{CliTheme.Success}]{Markup.Escape(sn)}[/]");
        }
        AnsiConsole.Write(table);
    }

    // ---- upload ----

    private async Task HandleUploadAsync(string[] parts)
    {
        switch (parts[1].ToLowerInvariant())
        {
            case "schema" when parts.Length >= 4:
                PrintJson(await Spinner($"Uploading schema '{parts[2]}'…",
                    () => dmart.UploadSchemaAsync(parts[2], parts[3])));
                break;
            case "csv" when parts.Length >= 6:
                PrintJson(await Spinner($"Uploading CSV '{parts[5]}'…",
                    () => dmart.UploadCsvAsync(parts[2], parts[3], parts[4], parts[5])));
                break;
            default:
                LastCommandFailed = true;
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Usage:")} upload schema <name> <file> | upload csv <type> <subpath> <schema> <file>");
                break;
        }
    }

    // ---- whoami / version ----

    private void PrintWhoami()
    {
        if (CliTheme.JsonOnly)
        {
            // Hand-built JSON to keep this AOT-safe (no reflection serializer).
            Console.WriteLine($"{{\"user\":\"{JsonStr(settings.Shortname)}\"," +
                              $"\"server\":\"{JsonStr(settings.Url)}\"," +
                              $"\"space\":\"{JsonStr(dmart.CurrentSpace)}\"," +
                              $"\"subpath\":\"{JsonStr(dmart.CurrentSubpath)}\"}}");
            return;
        }
        var t = new Table().Border(TableBorder.Minimal).HideHeaders();
        t.AddColumn("k"); t.AddColumn("v");
        t.AddRow($"[{CliTheme.Muted}]user[/]",    $"[{CliTheme.Heading}]{Markup.Escape(settings.Shortname)}[/]");
        t.AddRow($"[{CliTheme.Muted}]server[/]",  $"[{CliTheme.Path}]{Markup.Escape(settings.Url)}[/]");
        t.AddRow($"[{CliTheme.Muted}]space[/]",   $"[{CliTheme.Heading}]{Markup.Escape(dmart.CurrentSpace)}[/]");
        t.AddRow($"[{CliTheme.Muted}]subpath[/]", $"[{CliTheme.Path}]{Markup.Escape(dmart.CurrentSubpath)}[/]");
        AnsiConsole.Write(t);
    }

    private async Task PrintVersionAsync()
    {
        var info = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev";
        var manifest = await Spinner("Fetching server manifest…", () => dmart.ManifestAsync());
        string? sv = null, sb = null;
        if (manifest is JsonElement m && m.ValueKind == JsonValueKind.Object)
        {
            if (m.TryGetProperty("version", out var v)) sv = v.GetString();
            if (m.TryGetProperty("branch", out var b))  sb = b.GetString();
        }
        if (CliTheme.JsonOnly)
        {
            Console.WriteLine($"{{\"cli_build\":\"{JsonStr(info)}\"," +
                              $"\"server_version\":\"{JsonStr(sv ?? "")}\"," +
                              $"\"server_branch\":\"{JsonStr(sb ?? "")}\"}}");
            return;
        }
        var t = new Table().Border(TableBorder.Minimal).HideHeaders();
        t.AddColumn("k"); t.AddColumn("v");
        t.AddRow($"[{CliTheme.Muted}]CLI build[/]", $"[{CliTheme.Success}]{Markup.Escape(info)}[/]");
        if (sv is not null) t.AddRow($"[{CliTheme.Muted}]server version[/]", $"[{CliTheme.Success}]{Markup.Escape(sv)}[/]");
        if (sb is not null) t.AddRow($"[{CliTheme.Muted}]server branch[/]",  $"[{CliTheme.Heading}]{Markup.Escape(sb)}[/]");
        AnsiConsole.Write(t);
    }

    private static string JsonStr(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    // ---- helpers ----

    private async Task<bool> SwitchSpaceAsync(string prefix)
    {
        foreach (var name in dmart.SpaceNames)
        {
            if (name.StartsWith(prefix))
            {
                CliTheme.Line($"Switching space to {CliTheme.Wrap(CliTheme.Heading, name)}");
                dmart.CurrentSpace = name;
                dmart.CurrentSubpath = "/";
                return true;
            }
        }
        return false;
    }

    private void PrintSpaces()
    {
        var rendered = dmart.SpaceNames.Select(s =>
            s == dmart.CurrentSpace
                ? $"[bold {CliTheme.Heading}]{Markup.Escape(s)}[/]"
                : $"[{CliTheme.Cmd}]{Markup.Escape(s)}[/]");
        CliTheme.Line($"Available spaces: {string.Join("  ", rendered)}");
    }

    private static (string? Space, string? Subpath) ParseSpaceRef(string target)
    {
        // @space/subpath
        var clean = target.TrimStart('@');
        var slash = clean.IndexOf('/');
        if (slash < 0) return (clean, null);
        return (clean[..slash], clean[(slash + 1)..]);
    }

    // Run an async op under a Spectre Status spinner when interactive; fall
    // through to a plain await otherwise (script mode, output-redirected).
    private static async Task<T> Spinner<T>(string label, Func<Task<T>> op)
    {
        if (CliTheme.JsonOnly || Console.IsOutputRedirected) return await op();
        T result = default!;
        await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .StartAsync(label, async _ => { result = await op(); });
        return result;
    }

    private static void PrintJson(JsonElement json)
    {
        if (CliTheme.JsonOnly || !CliTheme.ColorEnabled)
        {
            // Compact-then-pretty: System.Text.Json's `Indented` is enough.
            // We round-trip via JsonSerializer using the AOT-safe overload.
            using var ms = new MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(ms,
                       new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                json.WriteTo(w);
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            return;
        }
        ColorizeJson(json, indent: 0);
        Console.WriteLine();
    }

    private static void ColorizeJson(JsonElement el, int indent)
    {
        var pad = new string(' ', indent * 2);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                Console.WriteLine("{");
                var props = el.EnumerateObject().ToList();
                for (var i = 0; i < props.Count; i++)
                {
                    var p = props[i];
                    Console.Write($"{pad}  [36m\"{p.Name}\"[0m: ");
                    ColorizeJson(p.Value, indent + 1);
                    Console.WriteLine(i < props.Count - 1 ? "," : "");
                }
                Console.Write($"{pad}}}");
                break;
            case JsonValueKind.Array:
                var items = el.EnumerateArray().ToList();
                if (items.Count == 0) { Console.Write("[]"); break; }
                Console.WriteLine("[");
                for (var i = 0; i < items.Count; i++)
                {
                    Console.Write($"{pad}  ");
                    ColorizeJson(items[i], indent + 1);
                    Console.WriteLine(i < items.Count - 1 ? "," : "");
                }
                Console.Write($"{pad}]");
                break;
            case JsonValueKind.String:
                var s = el.GetString()!;
                if (s is "success") Console.Write($"[32m\"{s}\"[0m");
                else if (s is "failed" or "error") Console.Write($"[31m\"{s}\"[0m");
                else Console.Write($"[33m\"{s}\"[0m");
                break;
            case JsonValueKind.Number:
                Console.Write($"[35m{el}[0m");
                break;
            case JsonValueKind.True:
                Console.Write("[32mtrue[0m");
                break;
            case JsonValueKind.False:
                Console.Write("[31mfalse[0m");
                break;
            case JsonValueKind.Null:
                Console.Write("[90mnull[0m");
                break;
            default:
                Console.Write(el.ToString());
                break;
        }
    }

    // ---- help ----

    private static void PrintHelp(string? specific)
    {
        if (specific is not null)
        {
            if (CommandHelp.TryGetValue(specific, out var entry))
            {
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Cmd, specific)}: {Markup.Escape(entry.Summary)}");
                CliTheme.Line($"  {CliTheme.Wrap(CliTheme.Muted, "usage:")} {Markup.Escape(entry.Usage)}");
            }
            else
            {
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Unknown command:")} {CliTheme.Escape(specific)}");
                var s = SuggestCommand(specific);
                if (s is not null)
                    CliTheme.Line($"  did you mean {CliTheme.Wrap(CliTheme.Cmd, s)}?");
            }
            return;
        }
        var table = new Table().Border(TableBorder.Rounded).Title("DMart CLI Help");
        table.AddColumn("Command");
        table.AddColumn("Description");
        foreach (var (name, (usage, summary)) in CommandHelp)
        {
            table.AddRow($"[{CliTheme.Cmd}]{Markup.Escape(usage)}[/]", Markup.Escape(summary));
        }
        AnsiConsole.Write(table);
    }

    // Damerau-Levenshtein distance ≤ 2 → suggestion. Cheap enough for the
    // ~20 commands we register.
    private static string? SuggestCommand(string typed)
    {
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var name in CommandHelp.Keys)
        {
            var d = LevenshteinDistance(typed.ToLowerInvariant(), name.ToLowerInvariant());
            if (d < bestDist) { bestDist = d; best = name; }
        }
        return bestDist <= 2 ? best : null;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
