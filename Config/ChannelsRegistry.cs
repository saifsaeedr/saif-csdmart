using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Dmart.Config;

// Source-generated JSON metadata for the channels config — keeps the loader
// AOT-clean (no reflection-based JsonSerializer.Deserialize at runtime).
[JsonSerializable(typeof(List<ChannelsRegistry.RawChannel>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal partial class ChannelsConfigJsonContext : JsonSerializerContext;

// Holds the parsed `.dmart/channels.json` registry used by ChannelAuthMiddleware.
// Mirrors dmart Python's utils/settings.py::load_config_files — the JSON is
// loaded once at startup and the `allowed_api_patterns` are pre-compiled to
// Regex so middleware avoids per-request compilation cost.
//
// When EnableChannelAuth is false, this loader still runs (cheap) but the
// middleware short-circuits before consulting the registry. A missing file
// is silent (most common case — feature disabled, no config); a present
// but unparseable file logs an error and yields an empty channel list.
public sealed class ChannelsRegistry
{
    public IReadOnlyList<Channel> Channels { get; }

    public ChannelsRegistry(IOptions<DmartSettings> options, ILogger<ChannelsRegistry> log)
    {
        var s = options.Value;
        Channels = Load(s.ChannelsConfigPath, log);
    }

    private static IReadOnlyList<Channel> Load(string configuredPath, ILogger log)
    {
        var path = ResolvePath(configuredPath);
        if (path is null || !File.Exists(path)) return Array.Empty<Channel>();

        try
        {
            var raw = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize(raw, ChannelsConfigJsonContext.Default.ListRawChannel);
            if (entries is null) return Array.Empty<Channel>();

            var result = new List<Channel>(entries.Count);
            foreach (var e in entries)
            {
                // Per-pattern compile so a single bad regex (catastrophic
                // backtracking, invalid syntax) doesn't poison the whole
                // channel — log it and drop just that pattern. The 100ms
                // match timeout is the runtime backstop: every IsMatch call
                // aborts past that, defending the gate from a pattern that
                // *compiles* but blows up at match time on attacker-shaped
                // paths. Without it, a single ReDoS-prone entry in
                // ~/.dmart/channels.json could hang every request thread.
                var patternList = new List<Regex>();
                foreach (var p in e.AllowedApiPatterns ?? new List<string>())
                {
                    try
                    {
                        patternList.Add(new Regex(p,
                            RegexOptions.Compiled | RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(100)));
                    }
                    catch (ArgumentException ex)
                    {
                        log.LogError(ex, "channels: invalid regex {Pattern} on channel {Channel} — dropped",
                            p, e.Name ?? "");
                    }
                }
                result.Add(new Channel(
                    e.Name ?? "",
                    e.Keys ?? new List<string>(),
                    patternList.ToArray()));
            }
            return result;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to load channels config at {Path}", path);
            return Array.Empty<Channel>();
        }
    }

    private static string? ResolvePath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        return Path.Combine(home, ".dmart", "channels.json");
    }

    internal sealed class RawChannel
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("keys")] public List<string>? Keys { get; set; }
        [JsonPropertyName("allowed_api_patterns")] public List<string>? AllowedApiPatterns { get; set; }
    }

    public sealed record Channel(string Name, IReadOnlyList<string> Keys, Regex[] AllowedApiPatterns);
}
