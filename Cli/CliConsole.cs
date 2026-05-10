namespace Dmart.Cli;

internal static class CliConsole
{
    // ANSI-colorized JSON output for terminal display.
    // Cyan=keys, Yellow=strings, Magenta=numbers, Green=true, Red=false, Gray=null.
    public static void PrintColorJson(System.Text.Json.JsonElement el, int indent)
    {
        var pad = new string(' ', indent * 2);
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                Console.WriteLine("{");
                var props = el.EnumerateObject().ToList();
                for (var i = 0; i < props.Count; i++)
                {
                    Console.Write($"{pad}  \u001b[36m\"{props[i].Name}\"\u001b[0m: ");
                    PrintColorJson(props[i].Value, indent + 1);
                    Console.WriteLine(i < props.Count - 1 ? "," : "");
                }
                Console.Write($"{pad}}}");
                break;
            case System.Text.Json.JsonValueKind.Array:
                var items = el.EnumerateArray().ToList();
                if (items.Count == 0) { Console.Write("[]"); break; }
                Console.WriteLine("[");
                for (var i = 0; i < items.Count; i++)
                {
                    Console.Write($"{pad}  ");
                    PrintColorJson(items[i], indent + 1);
                    Console.WriteLine(i < items.Count - 1 ? "," : "");
                }
                Console.Write($"{pad}]");
                break;
            case System.Text.Json.JsonValueKind.String:
                Console.Write($"\u001b[33m\"{el.GetString()}\"\u001b[0m");
                break;
            case System.Text.Json.JsonValueKind.Number:
                Console.Write($"\u001b[35m{el}\u001b[0m");
                break;
            case System.Text.Json.JsonValueKind.True:
                Console.Write("\u001b[32mtrue\u001b[0m");
                break;
            case System.Text.Json.JsonValueKind.False:
                Console.Write("\u001b[31mfalse\u001b[0m");
                break;
            case System.Text.Json.JsonValueKind.Null:
                Console.Write("\u001b[90mnull\u001b[0m");
                break;
            default:
                Console.Write(el.ToString());
                break;
        }
    }
}
