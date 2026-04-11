using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Plugins;

// Port of dmart/backend/utils/plugin_manager.py::PluginManager.
//
// Responsibilities:
//   1. At startup, scan {BaseDir}/plugins/<name>/config.json files, decode each
//      into a PluginWrapper, match the shortname against DI-registered hook +
//      API plugin instances, and build indexed before/after dispatch tables.
//   2. Expose BeforeActionAsync / AfterActionAsync so EntryService (and future
//      handlers) can fire events at the corresponding points in a request.
//   3. Filter events against each plugin's config.json filters (subpaths with
//      hierarchical startswith semantics, resource_types, schema_shortnames,
//      actions) before invoking a plugin.
//   4. Gate dispatch by the event's Space.ActivePlugins list (matching Python).
//   5. Run after-action concurrent plugins as fire-and-forget background tasks
//      so slow hooks don't block the response.
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
    SpaceRepository spaces,
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

    // Short-lived cache for space lookups, matching Python's 2-second cache so
    // a before+after pair for the same request doesn't double-fetch the space
    // row. Keyed by space name.
    private readonly Dictionary<string, (long Ticks, Space? Space)> _spaceCache = new(StringComparer.Ordinal);
    private const long SpaceCacheTtlTicks = 2 * TimeSpan.TicksPerSecond;

    public IReadOnlyList<string> ActivePlugins => _activePlugins;

    public IReadOnlyList<IApiPlugin> ActiveApiPlugins => _activeApiPlugins;

    // ========================================================================
    // LOAD
    // ========================================================================

    // Scans {BaseDir}/plugins/<plugin-name>/config.json. If the base plugin dir
    // doesn't exist (e.g. tests running out of a temp dir), just returns with
    // an empty dispatch table — that's a valid state.
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(root))
        {
            log.LogInformation("plugins dir not found at {Root} — no plugins loaded", root);
            return;
        }

        var configs = new List<PluginWrapper>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;
            try
            {
                var bytes = await File.ReadAllBytesAsync(configPath, ct);
                var wrapper = JsonSerializer.Deserialize(bytes, DmartJsonContext.Default.PluginWrapper);
                if (wrapper is null) continue;
                // Python overwrites shortname with the directory basename. Mirror
                // that so config files don't have to spell it twice.
                wrapper.Shortname = Path.GetFileName(dir);
                configs.Add(wrapper);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "PLUGIN_ERROR: failed to parse {Config}", configPath);
            }
        }

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
                        log.LogInformation("PLUGIN_LOADED: {Shortname} (api)", w.Shortname);
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
                    foreach (var actionStr in w.Filters.Actions)
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
                    log.LogInformation("PLUGIN_LOADED: {Shortname} (hook, {Listen})", w.Shortname, w.ListenTime);
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
        var spaceActive = await GetSpaceActivePluginsAsync(e.SpaceName, ct);
        if (spaceActive is null) return;

        foreach (var hook in plugins)
        {
            if (!spaceActive.Contains(hook.Wrapper.Shortname)) continue;
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
        if (!_after.TryGetValue(e.ActionType, out var plugins)) return;
        var spaceActive = await GetSpaceActivePluginsAsync(e.SpaceName, ct);
        if (spaceActive is null) return;

        foreach (var hook in plugins)
        {
            if (!spaceActive.Contains(hook.Wrapper.Shortname)) continue;
            if (hook.Wrapper.Filters is null) continue;
            if (!MatchedFilters(hook.Wrapper.Filters, e)) continue;

            if (hook.Wrapper.Concurrent)
            {
                // Fire-and-forget so slow hooks don't delay the response. We still
                // log any failures from the background task — no swallowed errors.
                var captured = hook;
                _ = Task.Run(async () =>
                {
                    try { await captured.Plugin.HookAsync(e, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "PLUGIN_ERROR: {Shortname} after-hook failed (async)", captured.Wrapper.Shortname);
                    }
                }, CancellationToken.None);
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
    // FILTER MATCHING (mirrors Python matched_filters)
    // ========================================================================

    internal static bool MatchedFilters(EventFilter filters, Event e)
    {
        // Python builds two candidate subpath forms: the event's subpath and the
        // same with/without a leading slash. We normalize the same way.
        var subpaths = new List<string>(2) { e.Subpath };
        if (!string.IsNullOrEmpty(e.Subpath) && e.Subpath[0] == '/')
            subpaths.Add(e.Subpath[1..]);
        else
            subpaths.Add("/" + e.Subpath);

        if (!filters.Subpaths.Contains("__ALL__") &&
            !subpaths.Any(sp => filters.Subpaths.Any(fp => SubpathMatches(sp, fp))))
            return false;

        // Content resources also gate on schema_shortname when the filter lists
        // schemas — other resource kinds skip this rule.
        if (e.ResourceType == ResourceType.Content
            && !filters.SchemaShortnames.Contains("__ALL__")
            && (e.SchemaShortname is null || !filters.SchemaShortnames.Contains(e.SchemaShortname)))
            return false;

        if (filters.ResourceTypes.Count > 0
            && !filters.ResourceTypes.Contains("__ALL__")
            && e.ResourceType is not null
            && !filters.ResourceTypes.Contains(JsonbHelpers.EnumMember(e.ResourceType.Value)))
            return false;

        return true;
    }

    // Python's hierarchical startswith: filter "foo" matches event subpath "foo",
    // "foo/bar", "foo/bar/baz" but not "foobar". A trailing slash on the filter
    // form is stripped so "foo/" and "foo" behave identically.
    private static bool SubpathMatches(string eventSubpath, string filterSubpath)
    {
        var normalized = filterSubpath.TrimEnd('/');
        if (eventSubpath == normalized) return true;
        return eventSubpath.StartsWith(normalized + "/", StringComparison.Ordinal);
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
    // SPACE LOOKUP (with Python-matching 2s cache)
    // ========================================================================

    // Returns the HashSet of active_plugins declared on the space row, or null
    // if the space doesn't exist (matches Python: "space is None → return").
    // Null active_plugins list is normalized to an empty set so no plugins fire
    // but the method still returns non-null (the dispatch loop then no-ops).
    private async Task<HashSet<string>?> GetSpaceActivePluginsAsync(string spaceName, CancellationToken ct)
    {
        var space = await GetSpaceCachedAsync(spaceName, ct);
        if (space is null) return null;
        return new HashSet<string>(space.ActivePlugins ?? new(), StringComparer.Ordinal);
    }

    private async Task<Space?> GetSpaceCachedAsync(string spaceName, CancellationToken ct)
    {
        var now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        lock (_spaceCache)
        {
            if (_spaceCache.TryGetValue(spaceName, out var cached) && now - cached.Ticks < SpaceCacheTtlTicks)
                return cached.Space;
        }
        var space = await spaces.GetAsync(spaceName, ct);
        lock (_spaceCache)
        {
            _spaceCache[spaceName] = (Environment.TickCount64 * TimeSpan.TicksPerMillisecond, space);
        }
        return space;
    }

    // ========================================================================
    // INTERNAL STATE
    // ========================================================================

    private sealed record LoadedHook(PluginWrapper Wrapper, IHookPlugin Plugin);
}
