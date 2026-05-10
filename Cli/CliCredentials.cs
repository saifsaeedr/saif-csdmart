using Dmart.Config;

namespace Dmart.Cli;

internal static class CliCredentials
{
    // Updates ~/.dmart/cli.ini with the new password, but only if the file
    // already exists and its shortname= matches the user being reset.
    public static void UpdateCliIni(string shortname, string password, DmartSettings s)
    {
        var dmartHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dmart");
        var cliIniPath = Path.Combine(dmartHome, "cli.ini");

        if (!File.Exists(cliIniPath))
        {
            Console.WriteLine($"Skipped {cliIniPath} (file does not exist)");
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(cliIniPath))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            values[t[..eq].Trim()] = t[(eq + 1)..].Trim().Trim('"').Trim('\'');
        }

        if (!values.TryGetValue("shortname", out var existing)
            || !string.Equals(existing, shortname, StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"Skipped {cliIniPath} (shortname='{existing ?? ""}' does not match '{shortname}')");
            return;
        }

        values["password"] = password;
        if (!values.ContainsKey("url"))
            values["url"] = $"http://{s.ListeningHost}:{s.ListeningPort}";

        var lines = new List<string> { "# dmart-cli configuration (updated by dmart passwd)" };
        foreach (var (k, v) in values)
            lines.Add($"{k}={v}");
        File.WriteAllLines(cliIniPath, lines);
        Console.WriteLine($"Updated {cliIniPath} with credentials for {shortname}");
    }
}
