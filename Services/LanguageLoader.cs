using System.Reflection;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Services;

// Port of dmart Python's backend/languages/loader.py.
//
// Single source of truth for translated strings (invitation_message,
// reset_message, otp_message, state labels, ...). Loads from three sources:
//   1. Embedded resources whose name matches "*.languages.*.json" inside
//      this assembly — always present in the AOT release binary because
//      dmart.csproj does <EmbeddedResource Include="languages/*.json" />.
//   2. Filesystem fallback at {BaseDir}/languages and {Cwd}/languages —
//      lets tests / dotnet-run from source override without rebuilding.
//      Only fills file-level gaps left by the embedded pass.
//   3. ~/.dmart/languages/*.json — operator override that merges per-key
//      onto whatever (1)+(2) produced, so a deployment can change a single
//      message without shipping a full locale file.
// Shape mirrors Python's `languages: dict[str, dict[str, str]]`, keyed by
// the file stem ("english", "arabic", "kurdish").
public sealed class LanguageLoader(ILogger<LanguageLoader> log)
{
    private Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        var loaded = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();

        // Strategy 1: embedded resources. Resource names follow MSBuild's
        // dotted convention: "{RootNamespace}.languages.{stem}.json" (e.g.
        // "dmart.languages.english.json"). We match on the ".languages."
        // segment so the loader is independent of the root namespace.
        try
        {
            var assembly = typeof(LanguageLoader).Assembly;
            const string marker = ".languages.";
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var idx = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var leaf = name[(idx + marker.Length)..]; // "english.json"
                if (leaf.Contains('.', StringComparison.Ordinal)
                    && !leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var stem = leaf[..^".json".Length];
                if (loaded.ContainsKey(stem)) continue;
                try
                {
                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream is null) continue;
                    var dict = JsonSerializer.Deserialize(stream, DmartJsonContext.Default.DictionaryStringString);
                    if (dict is not null) loaded[stem] = dict;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "language load failed: embedded {Name}", name);
                }
            }
            if (loaded.Count > 0) sources.Add("embedded");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "language load: embedded resource scan failed, falling back to filesystem");
        }

        // Strategy 2: filesystem fallback — only fills gaps left by the
        // embedded pass, so a partial override (e.g. a single locale) works.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "languages"),
            Path.Combine(Directory.GetCurrentDirectory(), "languages"),
        };
        foreach (var root in candidates)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.json"))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (loaded.ContainsKey(stem)) continue;
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var dict = JsonSerializer.Deserialize(bytes, DmartJsonContext.Default.DictionaryStringString);
                    if (dict is not null)
                    {
                        loaded[stem] = dict;
                        if (!sources.Contains("filesystem")) sources.Add("filesystem");
                    }
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "language load failed: {File}", file);
                }
            }
        }

        // Strategy 3: ~/.dmart/languages/ — operator override. Unlike (2),
        // this merges per-key into the existing dict so the user can change
        // a single string (e.g. otp_message) without copying the whole file.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var userRoot = Path.Combine(home, ".dmart", "languages");
            if (Directory.Exists(userRoot))
            {
                foreach (var file in Directory.EnumerateFiles(userRoot, "*.json"))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    try
                    {
                        var bytes = File.ReadAllBytes(file);
                        var dict = JsonSerializer.Deserialize(bytes, DmartJsonContext.Default.DictionaryStringString);
                        if (dict is null) continue;
                        if (loaded.TryGetValue(stem, out var existing))
                        {
                            foreach (var (k, v) in dict) existing[k] = v;
                        }
                        else
                        {
                            loaded[stem] = new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
                        }
                        if (!sources.Contains("~/.dmart")) sources.Add("~/.dmart");
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "language load failed: {File}", file);
                    }
                }
            }
        }

        _languages = loaded;
        if (loaded.Count == 0)
            log.LogWarning("languages not loaded — translations unavailable, callers fall back to keys");
        else
            log.LogInformation("languages loaded: {Count} ({Names}) from {Sources}",
                loaded.Count, string.Join(", ", loaded.Keys), string.Join("+", sources));
    }

    // Python parity: `languages[user.language][key]`. Returns the localized
    // string, falling back to English when the requested language has no
    // entry for the key (or the language file isn't loaded). Returns null
    // when neither the requested language nor English has the key — caller
    // decides what to do with a missing translation.
    public string? Get(Language lang, string key)
    {
        var stem = JsonbHelpers.EnumMember(lang);
        if (_languages.TryGetValue(stem, out var dict) && dict.TryGetValue(key, out var val))
            return val;
        if (!stem.Equals("english", StringComparison.OrdinalIgnoreCase)
            && _languages.TryGetValue("english", out var en) && en.TryGetValue(key, out var enVal))
            return enVal;
        return null;
    }
}
