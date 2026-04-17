using System.Reflection;

namespace Dmart.Config;

// Strict validation for config.env keys. Mirrors pydantic-settings'
// `extra = "forbid"` behavior: any key that doesn't map to a property on
// `DmartSettings` is a configuration error, not a silent fall-through.
// A silent fall-through hides typos (DATABAE_HOST vs DATABASE_HOST) and lets
// stale keys from renamed/removed settings linger unnoticed.
//
// Only `config.env` keys are validated — shell env vars and Dmart__* overrides
// are out of scope (they routinely contain unrelated entries).
public static class DotEnvStrictCheck
{
    // Keys that are allowed in config.env but don't map to a DmartSettings
    // property. Keep this list short — every entry here is a potential source
    // of silent config drift.
    private static readonly HashSet<string> AllowedNonDmartKeys = new(StringComparer.Ordinal)
    {
        // Path override for dotenv lookup — honored by DotEnv.FindConfigFile
        // before this validator runs, so it's expected to appear in some files.
        "BACKEND_ENV",
        "DMART_ENV",
    };

    /// <summary>
    /// Walks the key set parsed from config.env and returns a list of human
    /// readable error messages — one per unrecognized key. Empty list means
    /// the file is clean. The caller decides whether to print + exit.
    /// </summary>
    public static List<string> ValidateKeys(string configPath, IReadOnlyDictionary<string, string> rawKeys)
    {
        var validKeys = BuildValidKeySet();
        var errors = new List<string>();
        foreach (var key in rawKeys.Keys)
        {
            if (AllowedNonDmartKeys.Contains(key)) continue;
            if (validKeys.Contains(key)) continue;
            errors.Add($"unknown config key '{key}' in {configPath}");
        }
        return errors;
    }

    // Build the UPPER_SNAKE_CASE form of every writable public property on
    // DmartSettings, using the inverse of DotEnv.ToConfigurationKey. We do
    // this at runtime rather than hard-coding the list so adding a new
    // property never requires updating the allowlist.
    private static HashSet<string> BuildValidKeySet()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in typeof(DmartSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetSetMethod() is null) continue;
            set.Add(PascalToUpperSnake(prop.Name));
        }
        return set;
    }

    // Invert DotEnv.ToConfigurationKey: "DatabaseHost" → "DATABASE_HOST".
    // Split before each uppercase letter that follows a lowercase letter (or
    // at the start), then join with underscore and upper-case the whole thing.
    internal static string PascalToUpperSnake(string pascal)
    {
        var buffer = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(pascal[i - 1]) || (i + 1 < pascal.Length && char.IsLower(pascal[i + 1]))))
                buffer.Append('_');
            buffer.Append(char.ToUpperInvariant(c));
        }
        return buffer.ToString();
    }
}
