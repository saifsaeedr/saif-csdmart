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
        ["create"]   = ("create <name> <type> | create space <name>",
                                                            "Create entry of <type> at current subpath; or 'create space <name>'."),
        ["rm"]       = ("rm [--dry-run] [-f] <name|*>",    "Delete entry; --dry-run prints; -f skips confirm."),
        ["move"]     = ("move <type> <src> <dst>",         "Move resource."),
        ["mv"]       = ("mv <type> <src> <dst>",           "Alias for move."),
        ["print"]    = ("print <name>",                    "Print entry metadata (alias: p)."),
        ["cat"]      = ("cat <name | path/name | *>",      "Print entry data (alias: c)."),
        ["find"]     = ("find <pattern> [--type <rt>]",    "Search current space for pattern."),
        ["whoami"]   = ("whoami",                          "Show user, server, current space:subpath."),
        ["version"]  = ("version",                         "Show CLI build + server manifest."),
        ["attach"]   = ("attach <sn> <entry> <type> <file> [--name-en T] [--desc-en T] | attach --batch <entry> <type> <glob>",
                                                            "Upload attachment(s); --batch globs many files; per-locale name/desc."),
        ["upload"]   = ("upload schema <name> <file> | upload csv <type> <sub> <schema> <file>",
                                                            "Upload schema or CSV."),
        ["request"]  = ("request <json_file>",             "POST raw managed/request JSON."),
        ["progress"] = ("progress <sub> <sn> <action>",    "Progress a ticket."),
        ["import"]   = ("import <zip_file>",               "Import a ZIP archive."),
        ["export"]   = ("export [csv] (<query.json> | --space S [--subpath P] [--type T] [--limit N] [--from D] [--to D] [--all] [--include-self] [--out P])",
                                                            "Export to ZIP (or CSV) — query from file or from shortcut flags. --include-self also exports the parent folder of --subpath so a folder + its contents round-trip in one zip."),
        ["help"]     = ("help [command]",                  "Show this help, or details for one command."),
        ["exit"]     = ("exit | q | quit | Ctrl+D",        "Exit the CLI."),
    };

    public async Task ExecuteAsync(string input)
    {
        LastCommandFailed = false;
        var parts = SplitArgs(input);
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
                // Refresh the cached listing so a follow-up `cd <newdir>`
                // can find the just-created folder. Without this, the next
                // `cd` walks the stale CurrentEntries from the prior ls
                // and silently fails with "Folder not found".
                await dmart.ListAsync();
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

            case "attach" when parts.Length >= 2:
                await HandleAttachAsync(parts);
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
                await HandleExportAsync(parts);
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

    // ---- attach (single + batch + multilingual) ----

    private async Task HandleAttachAsync(string[] parts)
    {
        var batch = false;
        var displayname = new Dictionary<string, string>();
        var description = new Dictionary<string, string>();
        var positional = new List<string>();
        for (var i = 1; i < parts.Length; i++)
        {
            var a = parts[i];
            switch (a)
            {
                case "--batch": batch = true; break;
                case "--name-en" when i + 1 < parts.Length: displayname["en"] = parts[++i]; break;
                case "--name-ar" when i + 1 < parts.Length: displayname["ar"] = parts[++i]; break;
                case "--name-ku" when i + 1 < parts.Length: displayname["ku"] = parts[++i]; break;
                case "--desc-en" when i + 1 < parts.Length: description["en"] = parts[++i]; break;
                case "--desc-ar" when i + 1 < parts.Length: description["ar"] = parts[++i]; break;
                case "--desc-ku" when i + 1 < parts.Length: description["ku"] = parts[++i]; break;
                default: positional.Add(a); break;
            }
        }

        if (batch)
        {
            // attach --batch <entry> <type> <glob>
            if (positional.Count < 3)
            {
                LastCommandFailed = true;
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Usage:")} attach --batch <entry> <type> <glob>");
                return;
            }
            var (entry, payloadType, glob) = (positional[0], positional[1], positional[2]);
            var files = ResolveGlob(glob);
            if (files.Count == 0)
            {
                LastCommandFailed = true;
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Warning, "No files matched glob:")} {CliTheme.Escape(glob)}");
                return;
            }
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Heading, $"Uploading {files.Count} files…")}");
            // Per-locale name overrides apply to every file by default;
            // when no --name-en is given, the filename becomes the en label
            // so the catalog UI has something to render.
            await RunAttachBatch(files, entry, payloadType, displayname, description);
            return;
        }

        if (positional.Count < 4)
        {
            LastCommandFailed = true;
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Usage:")} attach <sn> <entry> <type> <file> [--name-en T] [--name-ar T] [--name-ku T] [--desc-en T] [--desc-ar T] [--desc-ku T]");
            return;
        }
        PrintJson(await Spinner($"Uploading {Path.GetFileName(positional[3])}…",
            () => dmart.AttachAsync(positional[0], positional[1], positional[2], positional[3],
                displayname.Count > 0 ? displayname : null,
                description.Count > 0 ? description : null)));
    }

    private async Task RunAttachBatch(List<string> files, string entry, string payloadType,
        Dictionary<string, string> displayname, Dictionary<string, string> description)
    {
        // Show a real Progress bar — every iteration ticks one task. Falls back
        // to silent iteration when output is redirected (Spectre's Progress
        // refuses to render to a non-tty and would otherwise dump escape soup).
        if (CliTheme.JsonOnly || Console.IsOutputRedirected)
        {
            foreach (var file in files) await UploadOne(file);
            return;
        }
        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(),
                     new PercentageColumn(), new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[grey]attach[/]", maxValue: files.Count);
                foreach (var file in files)
                {
                    task.Description = $"[grey]attach[/] [{CliTheme.Path}]{Markup.Escape(Path.GetFileName(file))}[/]";
                    await UploadOne(file);
                    task.Increment(1);
                }
            });

        async Task UploadOne(string file)
        {
            var sn = SanitizeShortname(Path.GetFileNameWithoutExtension(file));
            var dn = new Dictionary<string, string>(displayname);
            if (!dn.ContainsKey("en")) dn["en"] = Path.GetFileNameWithoutExtension(file);
            try
            {
                var resp = await dmart.AttachAsync(sn, entry, payloadType, file, dn,
                    description.Count > 0 ? description : null);
                if (CliTheme.JsonOnly) PrintJson(resp);
                if (resp.TryGetProperty("status", out var st) && st.GetString() != "success")
                    LastCommandFailed = true;
            }
            catch (Exception ex)
            {
                LastCommandFailed = true;
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "  upload failed:")} {CliTheme.Escape(file)}: {CliTheme.Escape(ex.Message)}");
            }
        }
    }

    // Resolve a path that may contain * or ? wildcards. Handles relative
    // and absolute paths; returns the input verbatim when no wildcards.
    private static List<string> ResolveGlob(string glob)
    {
        if (!glob.Contains('*') && !glob.Contains('?'))
            return File.Exists(glob) ? new List<string> { glob } : new List<string>();
        var dir = Path.GetDirectoryName(glob);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var pattern = Path.GetFileName(glob);
        if (string.IsNullOrEmpty(pattern)) pattern = "*";
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    // dmart shortnames must match ^[a-z0-9_]+$ — lower-case, swap spaces /
    // dashes / dots for underscores, drop everything else.
    private static string SanitizeShortname(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') sb.Append(c);
            else if (c is ' ' or '-' or '.') sb.Append('_');
        }
        if (sb.Length == 0) sb.Append("file");
        return sb.ToString();
    }

    // ---- export (zip + csv, file or flag-built query) ----

    private async Task HandleExportAsync(string[] parts)
    {
        var isCsv = parts.Length >= 2 && parts[1].Equals("csv", StringComparison.OrdinalIgnoreCase);
        var argStart = isCsv ? 2 : 1;
        var rest = parts.Skip(argStart).ToArray();
        if (rest.Length == 0)
        {
            LastCommandFailed = true;
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Usage:")} export [csv] <query.json> | export [csv] --space S [--subpath /sub] [--type T] [--limit N] [--from DATE] [--to DATE] [--all] [--include-self] [--out PATH]");
            return;
        }

        string queryJson;
        string? outPath = null;
        var includeSelf = false;
        var exportSpace = dmart.CurrentSpace;
        var exportSubpath = "/";
        if (rest[0].StartsWith("--"))
        {
            var (json, op, flags) = BuildExportQueryJson(rest);
            if (json is null) { LastCommandFailed = true; return; }
            queryJson = json;
            outPath = op;
            includeSelf = flags.IncludeSelf;
            exportSpace = flags.Space;
            exportSubpath = flags.Subpath;
        }
        else
        {
            // Positional path argument — read from disk.
            if (!File.Exists(rest[0]))
            {
                LastCommandFailed = true;
                CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Query file not found:")} {CliTheme.Escape(rest[0])}");
                return;
            }
            queryJson = await File.ReadAllTextAsync(rest[0]);
            // A trailing --out / --include-self can still apply on top of a file query.
            for (var i = 1; i < rest.Length; i++)
            {
                if (rest[i] == "--out" && i + 1 < rest.Length) outPath = rest[i + 1];
                if (rest[i] == "--include-self") includeSelf = true;
            }
        }

        var label = isCsv ? "Exporting CSV…" : "Exporting…";

        // CSV doesn't compose under --include-self (the merge logic targets
        // zip archives). Fail loudly rather than silently dropping the flag.
        if (isCsv && includeSelf)
        {
            LastCommandFailed = true;
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "--include-self is only meaningful for ZIP export, not CSV")}");
            return;
        }

        // Plain path — single fetch, single write.
        var subClean = exportSubpath.Trim('/');
        if (!includeSelf || string.IsNullOrEmpty(subClean))
        {
            var msg = isCsv
                ? await Spinner(label, () => dmart.ExportCsvAsync(queryJson, outPath))
                : await Spinner(label, () => dmart.ExportAsync(queryJson, outPath));
            CliTheme.Plain(msg);
            return;
        }

        // Two-pass: fetch parent folder meta + subtree, merge zips.
        var lastSlash = subClean.LastIndexOf('/');
        var leaf = lastSlash < 0 ? subClean : subClean[(lastSlash + 1)..];
        var parent = lastSlash < 0 ? "/" : subClean[..lastSlash];
        var folderQuery = BuildFolderQueryJson(exportSpace, parent, leaf);

        var (subtreeBytes, parentBytes) = await Spinner(label, async () => (
            await dmart.ExportToBytesAsync(queryJson),
            await dmart.ExportToBytesAsync(folderQuery)
        ));
        if (subtreeBytes is null || parentBytes is null)
        {
            LastCommandFailed = true;
            CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Export failed")} (one of the two passes returned a non-2xx)");
            return;
        }
        // The parent query at subpath=/ in the management space pulls every
        // user / role / permission alongside the folder meta we actually
        // need (server-side export branch — see ImportExportService). Strip
        // those down to just the leaf folder's tree + the space meta so the
        // merged zip stays focused on what --include-self promised.
        var leafPrefix = $"{exportSpace}/{subClean}/";
        var parentBytesTrimmed = FilterZip(parentBytes, path =>
            path == $"{exportSpace}/.dm/meta.space.json" || path.StartsWith(leafPrefix, StringComparison.Ordinal));
        var merged = MergeZips(parentBytesTrimmed, subtreeBytes);
        var msg2 = await dmart.WriteExportBytesAsync(merged, outPath, $"{exportSpace}.zip");
        CliTheme.Plain(msg2);
    }

    // Return a copy of `src` containing only entries whose FullName matches
    // `keep`. Used to drop unrelated rows from the --include-self parent
    // pass without touching the subtree zip.
    private static byte[] FilterZip(byte[] src, Func<string, bool> keep)
    {
        using var output = new MemoryStream();
        using (var outZip = new System.IO.Compression.ZipArchive(output,
                   System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var ms = new MemoryStream(src);
            using var inZip = new System.IO.Compression.ZipArchive(ms,
                System.IO.Compression.ZipArchiveMode.Read);
            foreach (var entry in inZip.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith("/")) continue;
                if (!keep(entry.FullName)) continue;
                var newEntry = outZip.CreateEntry(entry.FullName,
                    System.IO.Compression.CompressionLevel.Optimal);
                using var fromStream = entry.Open();
                using var toStream = newEntry.Open();
                fromStream.CopyTo(toStream);
            }
        }
        return output.ToArray();
    }

    // Build a query that grabs just the named entry sitting at parentSubpath.
    // Used by --include-self to fetch the folder meta that the main subtree
    // query (subpath=foo) wouldn't include because foo itself lives one
    // level up.
    private static string BuildFolderQueryJson(string space, string parentSubpath, string shortname)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"type\":\"search\"");
        sb.Append($",\"space_name\":\"{JsonStr(space)}\"");
        sb.Append($",\"subpath\":\"{JsonStr(parentSubpath)}\"");
        sb.Append($",\"filter_shortnames\":[\"{JsonStr(shortname)}\"]");
        sb.Append(",\"limit\":10,\"retrieve_json_payload\":true}");
        return sb.ToString();
    }

    // Merge two zip archives byte-for-byte. The first wins on duplicates
    // (typically `<space>/.dm/meta.space.json` shows up in both — we want
    // exactly one copy). Output is a fresh archive in memory.
    private static byte[] MergeZips(byte[] first, byte[] second)
    {
        using var output = new MemoryStream();
        using (var outZip = new System.IO.Compression.ZipArchive(output,
                   System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var src in new[] { first, second })
            {
                using var ms = new MemoryStream(src);
                using var inZip = new System.IO.Compression.ZipArchive(ms,
                    System.IO.Compression.ZipArchiveMode.Read);
                foreach (var entry in inZip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith("/")) continue;
                    if (!seen.Add(entry.FullName)) continue;
                    var newEntry = outZip.CreateEntry(entry.FullName,
                        System.IO.Compression.CompressionLevel.Optimal);
                    using var fromStream = entry.Open();
                    using var toStream = newEntry.Open();
                    fromStream.CopyTo(toStream);
                }
            }
        }
        return output.ToArray();
    }

    internal record struct ExportFlags(string Space, string Subpath, bool IncludeSelf);

    // Synthesize the JSON body for /managed/export and /managed/csv from
    // CLI flags. Defaults match the common case: search across the current
    // space at /, recursive, 10k records. Returns the parsed Space/Subpath/
    // IncludeSelf alongside the JSON so the caller can decide whether the
    // two-pass include-self path applies.
    private (string? Json, string? OutPath, ExportFlags Flags) BuildExportQueryJson(string[] args)
    {
        var space = dmart.CurrentSpace;
        var subpath = "/";
        string? type = null;
        var limit = 10_000;
        string? from = null, to = null;
        var all = false;
        var includeSelf = false;
        string? outPath = null;
        string? search = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--space"   when i + 1 < args.Length: space = args[++i]; break;
                case "--subpath" when i + 1 < args.Length: subpath = args[++i]; break;
                case "--type"    when i + 1 < args.Length: type = args[++i]; break;
                case "--limit"   when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var l)) limit = l;
                    break;
                case "--from"    when i + 1 < args.Length: from = args[++i]; break;
                case "--to"      when i + 1 < args.Length: to = args[++i]; break;
                case "--search"  when i + 1 < args.Length: search = args[++i]; break;
                case "--out"     when i + 1 < args.Length: outPath = args[++i]; break;
                case "--all":
                    all = true;
                    limit = 1_000_000;  // Mirrors catalog's downloadAll behavior.
                    break;
                case "--include-self":
                    includeSelf = true;
                    break;
                default:
                    CliTheme.Line($"{CliTheme.Wrap(CliTheme.Error, "Unknown export flag:")} {CliTheme.Escape(args[i])}");
                    return (null, null, default);
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("{\"type\":\"search\"");
        sb.Append($",\"space_name\":\"{JsonStr(space)}\"");
        sb.Append($",\"subpath\":\"{JsonStr(subpath)}\"");
        sb.Append($",\"limit\":{limit}");
        sb.Append(",\"retrieve_json_payload\":true");
        if (type is not null)
            sb.Append($",\"filter_types\":[\"{JsonStr(type)}\"]");
        if (search is not null)
            sb.Append($",\"search\":\"{JsonStr(search)}\"");
        // --all clears date filters, matching the catalog modal's behavior.
        if (!all && from is not null) sb.Append($",\"from_date\":\"{JsonStr(from)}\"");
        if (!all && to is not null)   sb.Append($",\"to_date\":\"{JsonStr(to)}\"");
        sb.Append('}');
        return (sb.ToString(), outPath, new ExportFlags(space, subpath, includeSelf));
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

    // Tokenize a command line, honoring double-quoted segments so that
    // `--name-en "Alpha File"` arrives as one token instead of two. Single
    // quotes are treated the same way; backslash inside a quote escapes the
    // next character. Whitespace runs outside quotes are separators.
    internal static string[] SplitArgs(string input)
    {
        var args = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quote = '\0';
        var inToken = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < input.Length) { sb.Append(input[++i]); continue; }
                if (c == quote) { quote = '\0'; continue; }
                sb.Append(c);
                inToken = true;
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken) { args.Add(sb.ToString()); sb.Clear(); inToken = false; }
            }
            else
            {
                sb.Append(c);
                inToken = true;
            }
        }
        if (inToken) args.Add(sb.ToString());
        return args.ToArray();
    }

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
