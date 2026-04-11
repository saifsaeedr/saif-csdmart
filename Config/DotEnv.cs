namespace Dmart.Config;

// Mirrors dmart/backend/utils/settings.py::get_env_file + pydantic-settings'
// env_file handling. Python loads key=value pairs from a config.env in the
// following priority order:
//
//   1. $BACKEND_ENV pointing to a specific file (if it exists)
//   2. ./config.env in the current working directory
//   3. ~/.dmart/config.env in the user's home directory
//
// We then translate those entries into IConfiguration keys under the Dmart
// section so the existing Options<DmartSettings> pipeline picks them up with
// no extra work. Env vars and appsettings.json still override dotenv values,
// matching pydantic-settings' layered resolution.
//
// Deliberately hand-rolled (no Microsoft.Extensions.Configuration.Ini or a
// third-party dotenv package): the format is trivial, and we want to stay
// AOT-clean. The parser handles:
//   - Blank lines
//   - # line comments (and trailing # comments on unquoted values)
//   - KEY=value and KEY="value" and KEY='value'
//   - export KEY=value (the `export` prefix is ignored)
//   - Quoted values pass through verbatim (no \n unescaping — Python's
//     pydantic-settings does the same)
public static class DotEnv
{
    // Standard Python-compatible lookup order. Returns the first file that
    // exists or null if none do. Also honors DMART_ENV as an alias of
    // BACKEND_ENV — some dmart deployments use the former.
    public static string? FindConfigFile()
    {
        var backendEnv = Environment.GetEnvironmentVariable("BACKEND_ENV")
                      ?? Environment.GetEnvironmentVariable("DMART_ENV");

        // 1. Explicit path via env var — only accepted if the file actually
        //    exists. Python's behavior is "fall through" when the path is
        //    missing, and we copy that so a typo doesn't silently disable
        //    the other fallbacks.
        if (!string.IsNullOrEmpty(backendEnv) && File.Exists(backendEnv))
            return backendEnv;

        // 2. ./config.env in current working directory
        var cwdConfig = Path.Combine(Directory.GetCurrentDirectory(), "config.env");
        if (File.Exists(cwdConfig)) return cwdConfig;

        // 3. ~/.dmart/config.env
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var homeConfig = Path.Combine(home, ".dmart", "config.env");
            if (File.Exists(homeConfig)) return homeConfig;
        }

        return null;
    }

    // Parses a dotenv file at the given path and returns the raw KEY → value
    // dictionary (unchanged case). Returns an empty dict if the file doesn't
    // exist so callers don't need a null check. Malformed lines (no `=`) are
    // skipped silently, matching pydantic-settings' tolerance.
    public static Dictionary<string, string> Parse(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue;

            // Tolerate "export KEY=value" (shell-style) by stripping the prefix.
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line.Substring("export ".Length).TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();

            // Strip matching quote pair. Python's dotenv treats "" and '' as
            // opaque delimiters — the inside is NOT unescaped, so we match.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value.Substring(1, value.Length - 2);
            }
            else
            {
                // Unquoted value: strip a trailing # comment (pydantic-settings
                // behavior), but only if preceded by whitespace, so URLs with
                // fragments like http://host/#frag still survive.
                var hashIdx = value.IndexOf(" #", StringComparison.Ordinal);
                if (hashIdx >= 0) value = value.Substring(0, hashIdx).TrimEnd();
            }

            result[key] = value;
        }

        return result;
    }

    // Maps a dotenv KEY (UPPER_SNAKE_CASE) to the IConfiguration path we expose
    // it under. Everything goes under "Dmart:" in PascalCase so
    // Configure<DmartSettings> picks it up without any per-key glue. Examples:
    //   DATABASE_HOST   → Dmart:DatabaseHost
    //   JWT_SECRET      → Dmart:JwtSecret
    //   ADMIN_SHORTNAME → Dmart:AdminShortname
    public static string ToConfigurationKey(string dotenvKey)
    {
        var parts = dotenvKey.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var buffer = new System.Text.StringBuilder("Dmart:", capacity: 32);
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            buffer.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
                buffer.Append(part.Substring(1).ToLowerInvariant());
        }
        return buffer.ToString();
    }

    // Convenience: read the file via FindConfigFile(), parse it, and project to
    // the Dmart: prefixed form. Returns (null, empty-dict) if no file was
    // found so the caller can log the path + count.
    public static (string? Path, Dictionary<string, string?> Values) Load()
    {
        var path = FindConfigFile();
        var mapped = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (path is null) return (null, mapped);

        foreach (var kv in Parse(path))
            mapped[ToConfigurationKey(kv.Key)] = kv.Value;
        return (path, mapped);
    }
}
