using System.Collections;
using System.Reflection;

namespace Dmart.Config;

// Produces the "public view" of DmartSettings — every property the app loaded
// from config.env / env vars, with secrets redacted. Shared by:
//   * GET /info/settings          (HTTP endpoint for CXB and operators)
//   * `dmart settings` CLI output
// Keeping both on one projection means adding a new setting automatically
// surfaces in both places — no double-bookkeeping.
//
// SECURITY: any property whose value could leak credentials or signing keys
// MUST be listed in `RedactedProperties`. The redacted keys are still present
// in the output so the caller can see WHICH secrets are configured, but the
// value is replaced with a placeholder.
public static class SettingsSerializer
{
    // Property names (C# PascalCase) that must never be echoed verbatim.
    // PostgresConnection is here because connection strings routinely carry
    // "Password=..." inline.
    private static readonly HashSet<string> RedactedProperties = new(StringComparer.Ordinal)
    {
        nameof(DmartSettings.JwtSecret),
        nameof(DmartSettings.DatabasePassword),
        nameof(DmartSettings.AdminPassword),
        nameof(DmartSettings.PostgresConnection),
    };

    /// <summary>
    /// Snapshots the current DmartSettings as a snake_case Dictionary suitable
    /// for direct JSON serialization. Sensitive values are replaced with
    /// "***set***" (present + configured) or "" (unset).
    /// </summary>
    public static Dictionary<string, object> ToPublicDictionary(DmartSettings s)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in typeof(DmartSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetSetMethod() is null) continue;   // skip calculated props
            var snake = DotEnvStrictCheck.PascalToUpperSnake(prop.Name).ToLowerInvariant();
            var value = prop.GetValue(s);

            if (RedactedProperties.Contains(prop.Name))
            {
                // Don't leak the value — but do surface whether it's configured
                // so an operator can tell at a glance that e.g. JWT_SECRET is
                // set (without knowing what it is).
                var isSet = value switch
                {
                    null => false,
                    string str => !string.IsNullOrEmpty(str),
                    _ => true,
                };
                result[snake] = isSet ? "***set***" : "";
                continue;
            }

            result[snake] = NormalizeForJson(value);
        }
        // Admin shortname is hardcoded in AdminBootstrap — surface it here so
        // CXB and operators don't have to know that detail.
        result["admin_shortname"] = "dmart";
        return result;
    }

    // Convert values into forms the source-generated JSON context knows how
    // to write directly (string / number / bool / list of strings).
    private static object NormalizeForJson(object? value) => value switch
    {
        null => "",
        string str => str,
        bool b => b,
        int or long or short or byte => value,
        double or float or decimal => value,
        IEnumerable enumerable => enumerable.Cast<object?>()
            .Select(e => e?.ToString() ?? "").ToList(),
        _ => value.ToString() ?? "",
    };
}
