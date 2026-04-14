namespace Dmart.Cli;

// Mirrors Python cli.py Settings — loaded from cli.ini / env vars.
public sealed class CliSettings
{
    public string Url { get; set; } = "http://localhost:8282";
    public string Shortname { get; set; } = "dmart";
    public string Password { get; set; } = "xxxx";
    public int QueryLimit { get; set; } = 50;
    public bool RetrieveJsonPayload { get; set; } = true;
    public string DefaultSpace { get; set; } = "management";
    public int Pagination { get; set; } = 50;

    // Load from cli.ini (key=value, same format as config.env)
    public static CliSettings Load()
    {
        var s = new CliSettings();

        var iniPath = Environment.GetEnvironmentVariable("DMART_CLI_CONFIG")
            ?? FindIniFile();

        if (iniPath is not null && File.Exists(iniPath))
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                var key = trimmed[..eq].Trim().ToUpperInvariant();
                var val = trimmed[(eq + 1)..].Trim().Trim('"').Trim('\'');
                switch (key)
                {
                    case "URL": s.Url = val; break;
                    case "SHORTNAME": s.Shortname = val; break;
                    case "PASSWORD": s.Password = val; break;
                    case "QUERY_LIMIT": if (int.TryParse(val, out var ql)) s.QueryLimit = ql; break;
                    case "RETRIEVE_JSON_PAYLOAD": s.RetrieveJsonPayload = val is "true" or "1" or "True"; break;
                    case "DEFAULT_SPACE": s.DefaultSpace = val; break;
                    case "PAGINATION": if (int.TryParse(val, out var pg)) s.Pagination = pg; break;
                }
            }
        }

        // Env vars override ini
        if (Environment.GetEnvironmentVariable("DMART_URL") is { Length: > 0 } url) s.Url = url;
        if (Environment.GetEnvironmentVariable("DMART_SHORTNAME") is { Length: > 0 } sn) s.Shortname = sn;
        if (Environment.GetEnvironmentVariable("DMART_PASSWORD") is { Length: > 0 } pw) s.Password = pw;

        return s;
    }

    private static string? FindIniFile()
    {
        // 1. ./cli.ini
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "cli.ini");
        if (File.Exists(cwd)) return cwd;
        // 2. ~/.dmart/cli.ini
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var homeIni = Path.Combine(home, ".dmart", "cli.ini");
            if (File.Exists(homeIni)) return homeIni;
        }
        return null;
    }
}
