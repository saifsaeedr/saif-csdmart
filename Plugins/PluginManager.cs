using System.Reflection;
using System.Text.Json;
using Dmart.DataAdapters.Sql;  // JsonbHelpers — enum wire-name resolver.
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Plugins.Native;
using Dmart.Services;

namespace Dmart.Plugins;

// Routes dmart action-events to the registered hook plugins.
//
// Responsibilities:
//   1. At startup, scan {BaseDir}/plugins/<name>/config.json files, decode each
//      into a PluginWrapper, match the shortname against DI-registered hook +
//      API plugin instances, and build indexed before/after dispatch tables.
//   2. Expose BeforeActionAsync / AfterActionAsync so EntryService (and future
//      handlers) can fire events at the corresponding points in a request.
//   3. Filter events against each plugin's `filters` block — subpaths keyed by
//      space (with __all_spaces__ / __all_subpaths__ / __current_user__
//      sentinels), resource_types, schema_shortnames, actions. The filter
//      vocabulary mirrors the permission engine so authors learn one model.
//
// Per-space gating is the plugin's own concern: each plugin declares which
// (space, subpath) combinations it fires on. The previous Space.ActivePlugins
// opt-in list and the `always_active` PluginWrapper flag both went away in
// favor of self-declared filters.
//
// What's different from Python:
//   - .NET AOT forbids dynamic assembly loading, so there's no equivalent of
//     Python's importlib.find_spec + exec_module. Instead, the concrete plugin
//     classes live in Plugins/BuiltIn/ and are registered in DI; the shortname
//     in config.json is the link from disk config to C# instance.
//   - Custom (user-authored) plugins that aren't registered in DI are logged
//     as "unknown" and skipped rather than silently dropped.
public sealed class PluginManager(
    IEnumerable<IHookPlugin> hookPluginInstances,
    IEnumerable<IApiPlugin> apiPluginInstances,
    SpaceEventLogger eventLogger,
    ILogger<PluginManager> log)
{
    // shortname -> instance lookup for both kinds
    private readonly Dictionary<string, IHookPlugin> _hookByShortname =
        hookPluginInstances.ToDictionary(p => p.Shortname, p => p, StringComparer.Ordinal);
    private readonly Dictionary<string, IApiPlugin> _apiByShortname =
        apiPluginInstances.ToDictionary(p => p.Shortname, p => p, StringComparer.Ordinal);

    // Loaded + filtered hook wrappers, bucketed by ActionType to avoid per-dispatch
    // filtering. Mirrors the Python _before_plugins / _after_plugins dicts.
    private readonly Dictionary<ActionType, List<LoadedHook>> _before = new();
    private readonly Dictionary<ActionType, List<LoadedHook>> _after = new();

    // Also keep the API plugins that survived the is_active gate so routes can
    // be mounted after Build() in Program.cs.
    private readonly List<IApiPlugin> _activeApiPlugins = new();

    // Flat list of active shortnames, exposed to /info/manifest.
    private readonly List<string> _activePlugins = new();

    // Tracks fire-and-forget concurrent after-hooks so graceful shutdown can
    // wait for them. Drained by PluginDrainService on host stop.
    private readonly InFlightTracker _inflight = new();

    // Wait (bounded) for in-flight concurrent hooks to finish. Called from the
    // PluginDrainService hosted service during graceful shutdown.
    public Task<bool> DrainAsync(TimeSpan timeout, CancellationToken ct = default)
        => _inflight.DrainAsync(timeout, ct);

    // Parallel registry carrying the resolved version + type per active plugin.
    // Populated alongside _activePlugins in Register(); exposed to /info/plugins.
    private readonly List<PluginInfo> _activePluginInfos = new();

    public IReadOnlyList<string> ActivePlugins => _activePlugins;

    public IReadOnlyList<IApiPlugin> ActiveApiPlugins => _activeApiPlugins;

    // Per-plugin (shortname, version, type) view, populated during Register().
    // Exposed to the new GET /info/plugins endpoint. Order matches _activePlugins
    // (insertion order, which mirrors the wrapper sort by ordinal).
    public IReadOnlyList<PluginInfo> ActivePluginInfos => _activePluginInfos;

    // ========================================================================
    // LOAD
    // ========================================================================

    // Scans {BaseDir}/plugins/<plugin-name>/config.json. If the base plugin dir
    // doesn't exist (e.g. tests running out of a temp dir), just returns with
    // an empty dispatch table — that's a valid state.
    public async Task LoadAsync(CancellationToken ct = default)
    {
        // Scan ALL existing plugin directories and merge configs.
        // Built-in plugins, RPM-installed, and user plugins (~/.dmart/plugins/).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "plugins"),
            Path.Combine(AppContext.BaseDirectory, "plugins"),
            "/usr/lib/dmart/plugins",
            string.IsNullOrEmpty(home) ? "" : Path.Combine(home, ".dmart", "plugins"),
        };

        var configs = new List<PluginWrapper>();
        var scanned = false;

        foreach (var root in candidates)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            scanned = true;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var configPath = Path.Combine(dir, "config.json");
                if (!File.Exists(configPath)) continue;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(configPath, ct);

                    // Detect the legacy flat-array `subpaths: [...]` shape
                    // and emit a clear migration error before the JSON
                    // deserializer fails with a generic conversion exception.
                    if (HasLegacySubpathsShape(bytes))
                    {
                        log.LogError(
                            "PLUGIN_ERROR: {Config} uses the legacy flat 'subpaths' array. " +
                            "Convert to the permission-style dict, e.g. " +
                            "\"subpaths\": {{ \"__all_spaces__\": [\"__all_subpaths__\"] }}. " +
                            "See custom_plugins_sdk/README.md for the migration guide.",
                            configPath);
                        continue;
                    }

                    var wrapper = JsonSerializer.Deserialize(bytes, DmartJsonContext.Default.PluginWrapper);
                    if (wrapper is null) continue;
                    wrapper.Shortname = Path.GetFileName(dir);
                    // Skip duplicates (first occurrence wins)
                    if (configs.Any(c => c.Shortname == wrapper.Shortname)) continue;
                    // Debug-level: config.json may contain secrets (API keys,
                    // tokens) for plugin-specific integrations. Operators
                    // who need this for plugin debugging can lower LOG_LEVEL
                    // to "debug" temporarily.
                    log.LogDebug("PLUGIN_CONFIG: {Shortname} from {Path} {Config}",
                        wrapper.Shortname, configPath, JsonUtil.Compact(bytes));
                    configs.Add(wrapper);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "PLUGIN_ERROR: failed to parse {Config}", configPath);
                }
            }
        }

        if (!scanned)
            log.LogInformation("plugins dir not found — no plugins loaded");

        Register(configs);
    }

    // Split out for unit testing: accepts pre-built wrappers without touching the
    // filesystem, so tests can drive the indexing logic directly.
    public void Register(IEnumerable<PluginWrapper> wrappers)
    {
        _before.Clear();
        _after.Clear();
        _activePlugins.Clear();
        _activeApiPlugins.Clear();
        _activePluginInfos.Clear();

        // Python loads in directory order then sorts by ordinal within action_type
        // buckets. We match that so "ordinal" ordering works across hook plugins.
        foreach (var w in wrappers.OrderBy(w => w.Ordinal))
        {
            if (!w.IsActive)
            {
                log.LogInformation("PLUGIN_SKIPPED: {Shortname} is_active=false", w.Shortname);
                continue;
            }

            switch (w.Type)
            {
                case PluginType.Api:
                    if (_apiByShortname.TryGetValue(w.Shortname, out var apiInstance))
                    {
                        _activeApiPlugins.Add(apiInstance);
                        _activePlugins.Add(w.Shortname);
                        var apiVersion = ResolveVersion(apiInstance);
                        _activePluginInfos.Add(new PluginInfo(w.Shortname, apiVersion, "api"));
                        // Version is rendered verbatim (no leading "v") so a
                        // git-describe-style "v0.8.68-…" doesn't double up to
                        // "vv0.8.68-…", and a plain "1.2.3" stays unadorned.
                        log.LogInformation("PLUGIN_LOADED: {Shortname} {Version} (api)", w.Shortname, apiVersion);
                    }
                    else
                    {
                        log.LogWarning("PLUGIN_UNKNOWN: {Shortname} (api) has no registered C# implementation", w.Shortname);
                    }
                    break;

                case PluginType.Hook:
                    if (!_hookByShortname.TryGetValue(w.Shortname, out var hookInstance))
                    {
                        log.LogWarning("PLUGIN_UNKNOWN: {Shortname} (hook) has no registered C# implementation", w.Shortname);
                        break;
                    }
                    if (w.Filters is null || w.ListenTime is null)
                    {
                        log.LogWarning("PLUGIN_INVALID: {Shortname} (hook) missing filters or listen_time", w.Shortname);
                        break;
                    }
                    var loaded = new LoadedHook(w, hookInstance);
                    var targetDict = w.ListenTime == EventListenTime.Before ? _before : _after;
                    // Empty Actions list ⇒ "every action" (mirrors the empty-list
                    // convention for resource_types and schema_shortnames).
                    var actionsToRegister = w.Filters.Actions.Count == 0
                        ? Enum.GetValues<ActionType>().Select(JsonbHelpers.EnumMember).ToList()
                        : w.Filters.Actions;
                    foreach (var actionStr in actionsToRegister)
                    {
                        if (!TryParseAction(actionStr, out var action)) continue;
                        if (!targetDict.TryGetValue(action, out var list))
                        {
                            list = new List<LoadedHook>();
                            targetDict[action] = list;
                        }
                        list.Add(loaded);
                    }
                    _activePlugins.Add(w.Shortname);
                    var hookVersion = ResolveVersion(hookInstance);
                    _activePluginInfos.Add(new PluginInfo(w.Shortname, hookVersion, "hook"));
                    log.LogInformation("PLUGIN_LOADED: {Shortname} {Version} (hook, {Listen})", w.Shortname, hookVersion, w.ListenTime);
                    break;

                default:
                    log.LogWarning("PLUGIN_INVALID: {Shortname} has no type", w.Shortname);
                    break;
            }
        }

        // Stable sort within each bucket by ordinal — LINQ OrderBy is stable, and
        // we already sorted the input above, but the bucket assignment order is
        // determined by insertion so we resort here to be explicit.
        foreach (var key in _before.Keys.ToList())
            _before[key] = _before[key].OrderBy(h => h.Wrapper.Ordinal).ToList();
        foreach (var key in _after.Keys.ToList())
            _after[key] = _after[key].OrderBy(h => h.Wrapper.Ordinal).ToList();
    }

    // ========================================================================
    // DISPATCH
    // ========================================================================

    public async Task BeforeActionAsync(Event e, CancellationToken ct = default)
    {
        if (!_before.TryGetValue(e.ActionType, out var plugins)) return;

        foreach (var hook in plugins)
        {
            if (hook.Wrapper.Filters is null) continue;
            if (!MatchedFilters(hook.Wrapper.Filters, e)) continue;

            // Before-action exceptions must propagate so the caller can fail the
            // originating request. Log + rethrow so we see it in structured logs.
            try
            {
                await hook.Plugin.HookAsync(e, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "PLUGIN_ERROR: {Shortname} before-hook failed", hook.Wrapper.Shortname);
                throw;
            }
        }
    }

    public async Task AfterActionAsync(Event e, CancellationToken ct = default)
    {
        // Audit-log every after-action event before plugin dispatch — this is
        // the parity hook for Python's spaces_folder/<space>/.dm/events.jsonl.log.
        // Logging first means the trail captures actions even if a plugin later
        // throws (it's an after-the-fact record of what already happened in PG).
        //
        // The audit trail itself is unconditional (no per-space gate) — the
        // only switch is global (DmartSettings.SpacesFolder being non-empty).
        // Operators who want per-space audit suppression should set
        // SpacesFolder="" and surface the trail elsewhere (e.g. PG history).
        await eventLogger.LogAsync(e, ct);

        if (!_after.TryGetValue(e.ActionType, out var plugins)) return;

        foreach (var hook in plugins)
        {
            if (hook.Wrapper.Filters is null) continue;
            if (!MatchedFilters(hook.Wrapper.Filters, e)) continue;

            if (hook.Wrapper.Concurrent)
            {
                // Fire-and-forget so slow hooks don't delay the response. We still
                // log any failures from the background task — no swallowed errors —
                // and track it so graceful shutdown can drain it (DrainAsync)
                // instead of cutting it off mid-write. Hooks observe the tracker's
                // ShutdownToken rather than CancellationToken.None.
                var captured = hook;
                _inflight.Track(Task.Run(async () =>
                {
                    try { await captured.Plugin.HookAsync(e, _inflight.ShutdownToken); }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "PLUGIN_ERROR: {Shortname} after-hook failed (async)", captured.Wrapper.Shortname);
                    }
                }, CancellationToken.None));
            }
            else
            {
                try
                {
                    await hook.Plugin.HookAsync(e, ct);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "PLUGIN_ERROR: {Shortname} after-hook failed", hook.Wrapper.Shortname);
                    // After-hook failures don't fail the originating action.
                }
            }
        }
    }

    // ========================================================================
    // FILTER MATCHING (permission-style subpath dictionary)
    // ========================================================================

    internal const string AllSpacesMw = "__all_spaces__";
    internal const string AllSubpathsMw = "__all_subpaths__";
    internal const string CurrentUserMw = "__current_user__";

    internal static bool MatchedFilters(EventFilter filters, Event e)
    {
        if (!MatchSpaceAndSubpath(filters.Subpaths, e)) return false;

        // Content resources also gate on schema_shortname when the filter
        // declares schemas. Empty list = "match every schema" (mirrors the
        // permission engine's empty-list-means-all convention).
        if (e.ResourceType == ResourceType.Content
            && filters.SchemaShortnames.Count > 0
            && (e.SchemaShortname is null || !filters.SchemaShortnames.Contains(e.SchemaShortname, StringComparer.Ordinal)))
            return false;

        // Empty resource_types = match every resource_type.
        if (filters.ResourceTypes.Count > 0
            && e.ResourceType is not null
            && !filters.ResourceTypes.Contains(JsonbHelpers.EnumMember(e.ResourceType.Value), StringComparer.Ordinal))
            return false;

        return true;
    }

    // Iterates the filter's per-space subpath patterns. Matches when the
    // event's (space, subpath) is covered by EITHER the literal space entry
    // OR the __all_spaces__ wildcard entry. Within each entry, an empty
    // patterns list is treated as "no patterns ⇒ no match" so authors must
    // explicitly opt in to "everything" via __all_subpaths__.
    private static bool MatchSpaceAndSubpath(Dictionary<string, List<string>> subpathDict, Event e)
    {
        if (subpathDict.Count == 0) return false;
        var normalizedEventSubpath = NormalizeEventSubpath(e.Subpath);

        if (subpathDict.TryGetValue(e.SpaceName, out var perSpace)
            && AnyPatternMatches(perSpace, normalizedEventSubpath, e.UserShortname))
            return true;

        if (subpathDict.TryGetValue(AllSpacesMw, out var anySpace)
            && AnyPatternMatches(anySpace, normalizedEventSubpath, e.UserShortname))
            return true;

        return false;
    }

    private static bool AnyPatternMatches(List<string> patterns, string eventSubpath, string? actor)
    {
        foreach (var raw in patterns)
        {
            if (raw == AllSubpathsMw) return true;

            var pattern = NormalizeFilterSubpath(raw);

            if (actor is not null && pattern.Contains(CurrentUserMw, StringComparison.Ordinal))
                pattern = pattern.Replace(CurrentUserMw, actor, StringComparison.Ordinal);

            if (SubpathMatches(eventSubpath, pattern)) return true;
        }
        return false;
    }

    // Drop the leading slash so filter and event use the same form. Empty /
    // root subpaths normalize to "" so they match a filter pattern of "".
    private static string NormalizeEventSubpath(string subpath)
    {
        if (string.IsNullOrEmpty(subpath) || subpath == "/") return "";
        return subpath.Trim('/');
    }

    private static string NormalizeFilterSubpath(string subpath)
    {
        if (string.IsNullOrEmpty(subpath) || subpath == "/") return "";
        return subpath.Trim('/');
    }

    // Hierarchical startswith: filter "foo" matches event subpath "foo",
    // "foo/bar", "foo/bar/baz" but not "foobar". A filter of "" matches
    // any event subpath at the space root and below — same as
    // __all_subpaths__ (which short-circuits earlier).
    private static bool SubpathMatches(string eventSubpath, string filterSubpath)
    {
        if (filterSubpath.Length == 0) return true;
        if (eventSubpath == filterSubpath) return true;
        return eventSubpath.StartsWith(filterSubpath + "/", StringComparison.Ordinal);
    }

    // Cheap shape probe: open the JSON, look for filters.subpaths, and check
    // whether it's an array (legacy shape) vs an object (new dict shape).
    // Anything that's neither — missing, null, malformed — falls through and
    // lets the regular deserializer surface its own error.
    internal static bool HasLegacySubpathsShape(byte[] configBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(configBytes);
            if (!doc.RootElement.TryGetProperty("filters", out var filters)) return false;
            if (filters.ValueKind != JsonValueKind.Object) return false;
            if (!filters.TryGetProperty("subpaths", out var subpaths)) return false;
            return subpaths.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseAction(string raw, out ActionType value)
    {
        // Match the [EnumMember] string, not the C# identifier.
        foreach (var a in Enum.GetValues<ActionType>())
        {
            if (JsonbHelpers.EnumMember(a) == raw)
            {
                value = a;
                return true;
            }
        }
        value = default;
        return false;
    }

    // ========================================================================
    // VERSION RESOLUTION
    // ========================================================================

    // Resolve a plugin's version following the same "baked into the binary"
    // model dmart uses for itself (see Api/Info/ManifestHandler.cs):
    //   1. Wrapper-supplied version (IPluginVersionSource): for native .so and
    //      subprocess plugins, the source of truth is the external artifact —
    //      the loader extracted it via dlsym(dmart_plugin_version) or from the
    //      info-response JSON, then handed it to the wrapper at construction.
    //   2. AssemblyInformationalVersion on the plugin's runtime-type assembly:
    //      for in-process .NET plugins (the BuiltIn classes plus any
    //      externally-loaded .dll), this reads the same attribute that
    //      Api/Info/ManifestHandler.cs reads on dmart's own assembly. Built-in
    //      plugins ship inside the dmart assembly so they inherit dmart's
    //      version automatically — no per-plugin override needed.
    //   3. AssemblyVersion fallback for assemblies that don't stamp the
    //      informational variant.
    //   4. "0.0.0" sentinel meaning "no version declared anywhere".
    internal static string ResolveVersion(object pluginInstance)
    {
        if (pluginInstance is IPluginVersionSource src && !string.IsNullOrEmpty(src.PluginVersion))
            return src.PluginVersion;

        var asm = pluginInstance.GetType().Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // Mirrors ManifestHandler.ResolveVersion: dmart's build pipeline
            // stamps "v0.8.70 branch=master date=2026-05-14" into the
            // informational version and we want just the leading version token.
            if (info.Contains("branch=", StringComparison.Ordinal))
                return info.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return info;
        }

        var simple = asm.GetName().Version?.ToString();
        if (!string.IsNullOrEmpty(simple)) return simple;

        return "0.0.0";
    }

    // ========================================================================
    // INTERNAL STATE
    // ========================================================================

    private sealed record LoadedHook(PluginWrapper Wrapper, IHookPlugin Plugin);
}

// Public surface for the new GET /info/plugins endpoint. Fields:
//   - Shortname: the plugin's stable identifier (matches config.json + dispatch)
//   - Version: the resolved version string (see PluginManager.ResolveVersion)
//   - Type: "hook" or "api" — the plugin's wire type
public sealed record PluginInfo(string Shortname, string Version, string Type);
