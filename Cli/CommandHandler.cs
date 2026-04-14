using System.Text.Json;
using Spectre.Console;

namespace Dmart.Cli;

// Mirrors Python cli.py's action() function — dispatches user commands.
public sealed class CommandHandler(DmartClient dmart, CliSettings settings)
{
    public async Task ExecuteAsync(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var oldSpace = dmart.CurrentSpace;
        var oldSubpath = dmart.CurrentSubpath;

        switch (parts[0].ToLowerInvariant())
        {
            case "h" or "help" or "?":
                PrintHelp();
                break;

            case "pwd":
                AnsiConsole.MarkupLine($"[yellow]{dmart.CurrentSpace}[/]:[blue]{dmart.CurrentSubpath}[/]");
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
                AnsiConsole.WriteLine(await dmart.ExportAsync(parts[1]));
                break;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(input)}");
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

        var path = dmart.CurrentSubpath == "/"
            ? $"[yellow]{dmart.CurrentSpace}[/]:[blue]/[/]"
            : $"[yellow]{dmart.CurrentSpace}[/]:[blue]{dmart.CurrentSubpath}[/]";
        AnsiConsole.MarkupLine(path);

        var page = (parts.Length >= 2 && int.TryParse(parts[^1], out var pg)) ? pg : settings.Pagination;
        var idx = 0;
        foreach (var entry in dmart.CurrentEntries)
        {
            var sn = entry.GetProperty("shortname").GetString() ?? "";
            var rt = entry.GetProperty("resource_type").GetString() ?? "";
            var icon = rt == "folder" ? ":file_folder:" : ":page_facing_up:";
            var extra = "";
            if (entry.TryGetProperty("attributes", out var attrs) &&
                attrs.ValueKind == JsonValueKind.Object &&
                attrs.TryGetProperty("payload", out var payload) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("content_type", out var ct))
            {
                var schema = payload.TryGetProperty("schema_shortname", out var ss) ? $",schema={ss}" : "";
                extra = $" [yellow](payload:type={ct}{schema})[/]";
            }
            AnsiConsole.MarkupLine($"{icon} [green]{Markup.Escape(sn)}[/]{extra}");
            idx++;
            if (idx >= page && idx < dmart.CurrentEntries.Count)
            {
                AnsiConsole.Write("q: quit, Enter: next page ");
                var key = Console.ReadKey(true);
                if (key.KeyChar == 'q') break;
                idx = 0;
            }
        }
    }

    // ---- cd ----

    private async Task HandleCdAsync(string[] parts)
    {
        if (parts.Length < 2)
        {
            dmart.CurrentSubpath = "/";
            await dmart.ListAsync();
            AnsiConsole.MarkupLine($"[yellow]Switched subpath to:[/] [green]{dmart.CurrentSubpath}[/]");
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
            AnsiConsole.MarkupLine($"[yellow]Switched subpath to:[/] [green]{dmart.CurrentSubpath}[/]");
            return;
        }

        if (target.StartsWith('@'))
        {
            var (space, subpath) = ParseSpaceRef(target);
            if (space is not null) await SwitchSpaceAsync(space);
            if (subpath is not null) dmart.CurrentSubpath = subpath;
            await dmart.ListAsync();
            AnsiConsole.MarkupLine($"[yellow]Switched subpath to:[/] [green]{dmart.CurrentSubpath}[/]");
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
                AnsiConsole.MarkupLine($"[yellow]Switched subpath to:[/] [green]{dmart.CurrentSubpath}[/]");
                return;
            }
        }
        AnsiConsole.MarkupLine($"[red]Folder not found:[/] {Markup.Escape(target)}");
    }

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
            AnsiConsole.MarkupLine($"[red]Space not found:[/] {Markup.Escape(parts[1])}");
        }
    }

    // ---- rm ----

    private async Task HandleRmAsync(string[] parts)
    {
        if (parts[1] == "*")
        {
            await dmart.ListAsync();
            foreach (var entry in dmart.CurrentEntries.ToList())
            {
                var sn = entry.GetProperty("shortname").GetString()!;
                var rt = entry.GetProperty("resource_type").GetString()!;
                AnsiConsole.Write($"{sn} ");
                PrintJson(await dmart.DeleteAsync(sn, rt));
            }
            return;
        }
        if (parts[1] == "space" && parts.Length >= 3)
        {
            PrintJson(await dmart.ManageSpaceAsync(parts[2], "delete"));
            return;
        }
        // Find by shortname
        var target = parts[1];
        foreach (var entry in dmart.CurrentEntries)
        {
            var sn = entry.GetProperty("shortname").GetString()!;
            var rt = entry.GetProperty("resource_type").GetString()!;
            if (sn == target)
            {
                PrintJson(await dmart.DeleteAsync(sn, rt));
                await dmart.ListAsync();
                return;
            }
        }
        AnsiConsole.MarkupLine("[yellow]Item not found[/]");
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
        AnsiConsole.MarkupLine("[yellow]Item not found[/]");
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
        AnsiConsole.MarkupLine("[yellow]Item not found[/]");
    }

    // ---- upload ----

    private async Task HandleUploadAsync(string[] parts)
    {
        switch (parts[1].ToLowerInvariant())
        {
            case "schema" when parts.Length >= 4:
                PrintJson(await dmart.UploadSchemaAsync(parts[2], parts[3]));
                break;
            case "csv" when parts.Length >= 6:
                PrintJson(await dmart.UploadCsvAsync(parts[2], parts[3], parts[4], parts[5]));
                break;
            default:
                AnsiConsole.MarkupLine("[red]Usage: upload schema <name> <file> | upload csv <type> <subpath> <schema> <file>[/]");
                break;
        }
    }

    // ---- helpers ----

    private async Task<bool> SwitchSpaceAsync(string prefix)
    {
        foreach (var name in dmart.SpaceNames)
        {
            if (name.StartsWith(prefix))
            {
                AnsiConsole.MarkupLine($"Switching space to [yellow]{name}[/]");
                dmart.CurrentSpace = name;
                dmart.CurrentSubpath = "/";
                return true;
            }
        }
        return false;
    }

    private void PrintSpaces()
    {
        var parts = dmart.SpaceNames.Select(s =>
            s == dmart.CurrentSpace ? $"[bold yellow]{s}[/]" : $"[blue]{s}[/]");
        AnsiConsole.MarkupLine($"Available spaces: {string.Join("  ", parts)}");
    }

    private static (string? Space, string? Subpath) ParseSpaceRef(string target)
    {
        // @space/subpath
        var clean = target.TrimStart('@');
        var slash = clean.IndexOf('/');
        if (slash < 0) return (clean, null);
        return (clean[..slash], clean[(slash + 1)..]);
    }

    private static void PrintJson(JsonElement json)
    {
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
                    Console.Write($"{pad}  \u001b[36m\"{p.Name}\"\u001b[0m: ");
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
                // Green for "success", red for "failed"/"error"
                if (s is "success") Console.Write($"\u001b[32m\"{s}\"\u001b[0m");
                else if (s is "failed" or "error") Console.Write($"\u001b[31m\"{s}\"\u001b[0m");
                else Console.Write($"\u001b[33m\"{s}\"\u001b[0m");
                break;
            case JsonValueKind.Number:
                Console.Write($"\u001b[35m{el}\u001b[0m");
                break;
            case JsonValueKind.True:
                Console.Write("\u001b[32mtrue\u001b[0m");
                break;
            case JsonValueKind.False:
                Console.Write("\u001b[31mfalse\u001b[0m");
                break;
            case JsonValueKind.Null:
                Console.Write("\u001b[90mnull\u001b[0m");
                break;
            default:
                Console.Write(el.ToString());
                break;
        }
    }

    private static void PrintHelp()
    {
        var table = new Table().Title("DMart CLI Help");
        table.AddColumn("Command");
        table.AddColumn("Description");
        table.AddRow("[blue]switch[/] [green]space[/]", "List spaces or switch to space");
        table.AddRow("[blue]ls[/] [green]path[/]", "List entries under current subpath");
        table.AddRow("[blue]pwd[/]", "Print current space:subpath");
        table.AddRow("[blue]cd[/] [green]folder[/]", "Enter folder (cd .. to go up)");
        table.AddRow("[blue]mkdir[/] [green]name[/]", "Create folder");
        table.AddRow("[blue]create[/] [green]space name | type name[/]", "Create space or entry");
        table.AddRow("[blue]rm[/] [green]name | *[/]", "Delete entry or all entries");
        table.AddRow("[blue]rm space[/] [green]name[/]", "Delete a space");
        table.AddRow("[blue]move[/] [green]type src dst[/]", "Move resource");
        table.AddRow("[blue]print[/] [green]name[/]", "Print entry metadata");
        table.AddRow("[blue]cat[/] [green]name[/]", "Print entry data");
        table.AddRow("[blue]attach[/] [green]sn entry type file[/]", "Upload attachment");
        table.AddRow("[blue]upload schema[/] [green]name file[/]", "Upload schema");
        table.AddRow("[blue]upload csv[/] [green]type sub schema file[/]", "Upload CSV data");
        table.AddRow("[blue]request[/] [green]json_file[/]", "Send raw request JSON");
        table.AddRow("[blue]progress[/] [green]sub sn action[/]", "Progress ticket");
        table.AddRow("[blue]import[/] [green]zip_file[/]", "Import ZIP archive");
        table.AddRow("[blue]export[/] [green]query_json[/]", "Export to ZIP");
        table.AddRow("[blue]exit | q | quit | Ctrl+D[/]", "Exit");
        table.AddRow("[blue]help | h | ?[/]", "Show this help");
        AnsiConsole.Write(table);
    }
}
