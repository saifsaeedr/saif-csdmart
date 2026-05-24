namespace Dmart.Config;

// Mirrors dmart/backend/utils/settings.py::get_env_file + pydantic-settings'
// env_file handling. Loads key=value pairs from a config.env in the
// following priority order:
//
//   1. $BACKEND_ENV (or $DMART_ENV) pointing to a specific file
//   2. ~/.dmart/config.env  (per-user install via `dmart init`)
//   3. /etc/dmart/config.env (system-wide RPM/DEB install)
//
// We then translate those entries into IConfiguration keys under the Dmart
// section so the existing Options<DmartSettings> pipeline picks them up with
// no extra work. Env vars (Dmart__Xxx) still override dotenv values,
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
    // exists AND passes the perms guard (Unix only — see AcceptablePerms),
    // or null if none do. Also honors DMART_ENV as an alias of BACKEND_ENV
    // — some dmart deployments use the former.
    public static string? FindConfigFile()
    {
        var backendEnv = Environment.GetEnvironmentVariable("BACKEND_ENV")
                      ?? Environment.GetEnvironmentVariable("DMART_ENV");

        // 1. Explicit path via env var — only accepted if the file actually
        //    exists. Python's behavior is "fall through" when the path is
        //    missing, and we copy that so a typo doesn't silently disable
        //    the other fallbacks. This is the dev workflow's escape hatch:
        //    set BACKEND_ENV=./config.env to point at a repo-local file
        //    without needing to drop one in ~/.dmart or /etc/dmart.
        if (!string.IsNullOrEmpty(backendEnv) && File.Exists(backendEnv)
            && AcceptablePerms(backendEnv))
            return backendEnv;

        // 2. ~/.dmart/config.env (per-user install via `dmart init`). Wins
        //    over the system-wide /etc/dmart/config.env so an operator
        //    with a custom per-user config isn't overridden by whatever
        //    the RPM/DEB package installed.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var homeConfig = Path.Combine(home, ".dmart", "config.env");
            if (File.Exists(homeConfig) && AcceptablePerms(homeConfig)) return homeConfig;
        }

        // 3. /etc/dmart/config.env (system-wide RPM/DEB install). Lets the
        //    operator run `dmart serve` / `dmart import` from any cwd
        //    instead of being forced to `cd /etc/dmart` first. Skipped on
        //    non-Unix-like filesystems where /etc isn't conventional.
        //
        // Note: cwd-relative config.env is NOT a lookup step. A stray
        // `config.env` in whatever directory the operator happens to be
        // in would silently override the intended config — an easy footgun
        // we'd rather not ship. Set BACKEND_ENV explicitly for repo-local
        // dev workflows.
        const string SystemConfig = "/etc/dmart/config.env";
        if (File.Exists(SystemConfig) && AcceptablePerms(SystemConfig)) return SystemConfig;

        return null;
    }

    // config.env carries JWT_SECRET, DATABASE_PASSWORD, ADMIN_PASSWORD —
    // any of those leaking to a non-admin local user is a complete-takeover
    // class of issue. Two-tier guard:
    //
    //   * world-writable (OtherWrite) → REFUSE. An attacker on the host
    //     could swap the file out from under dmart and steal traffic on
    //     the next restart. No safe fallback; we must not boot from this.
    //
    //   * world-readable (OtherRead) → WARN but continue. Strictly unsafe
    //     in multi-tenant hosts, but flipping this to a hard-refuse would
    //     break every existing operator the moment they upgrade — and a
    //     single-tenant host with 0644 config.env is the common dev/CI
    //     setup. Each boot prints the recommended chmod so the operator
    //     can tighten it without rolling back the upgrade.
    //
    // Group bits are tolerated unconditionally — the canonical
    // /etc/dmart/config.env deployment is owned root:dmart with 0640 perms
    // so the service user can read without exposing the file to "other".
    // No-op on Windows (security model is ACL-based).
    private static bool AcceptablePerms(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        UnixFileMode mode;
        try { mode = File.GetUnixFileMode(path); }
        catch { return true; }  // unknown FS / not readable — let the read attempt fail naturally

        if ((mode & UnixFileMode.OtherWrite) != 0)
        {
            Console.Error.WriteLine(
                $"refusing to read {path}: world-writable perms are catastrophic for a file " +
                $"that contains JWT_SECRET / ADMIN_PASSWORD / DATABASE_PASSWORD " +
                $"(current mode: 0{Convert.ToString((int)mode, 8)}). " +
                $"Run: chmod o-w {path}");
            return false;
        }
        if ((mode & UnixFileMode.OtherRead) != 0)
        {
            // Warn once per boot. We deliberately don't refuse — that would
            // break every operator on upgrade — but make the breadcrumb loud.
            Console.Error.WriteLine(
                $"warning: {path} is world-readable (mode 0{Convert.ToString((int)mode, 8)}) — " +
                $"contains secrets, recommend `chmod o-rwx {path}` to tighten.");
        }
        return true;
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
