using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Append-only audit log: one JSON object per line at
// "{SpacesFolder}/{space}/.dm/events.jsonl". Mirrors Python dmart's
// action_log plugin (backend/plugins/action_log/plugin.py) which writes the
// same file on every action. The C# port keeps real data in PostgreSQL, so
// this file is purely a parity artifact for downstream tooling that tails it
// (or for ops doing forensic timelines).
//
// Disabled when SpacesFolder is empty — emitting silently no-ops so we don't
// fail an action because the audit sink is missing. Logs and continues on any
// I/O error for the same reason: an action that succeeded in PG must not be
// reported to the client as failed because /var/lib/dmart was read-only.
//
// Wire shape (matches Python's models.core.Action.model_dump_json), top-level
// keys in this order:
//   { "resource": { uuid, type, space_name, subpath, shortname, schema_shortname,
//                   displayname, description, tags },
//     "user_shortname": "...",
//     "request": "create" | "update" | "delete" | ...,
//     "timestamp": "yyyy-MM-ddTHH:mm:ss.ffffff",
//     "attributes": { ... } }
public sealed class SpaceEventLogger(
    IOptions<DmartSettings> settings,
    ILogger<SpaceEventLogger> log,
    IHttpContextAccessor? httpContextAccessor = null)
{
    // Header names Python's get_request_data() excludes from request_headers.
    // Authorization carries the bearer token, Cookie carries session state —
    // both are credentials we never want to persist into an audit log.
    private static readonly HashSet<string> _excludedHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "cookie", "authorization" };

    // Per-space write lock: multiple concurrent actions in the same space must
    // not interleave bytes inside one JSON line. Different spaces hit different
    // files, so they don't contend. ConcurrentDictionary.GetOrAdd is the
    // lock-free idiom — at worst two threads racing on the same new space
    // create one extra SemaphoreSlim that's immediately discarded; the cost
    // is bounded by the number of distinct spaces ever seen by this process.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.Ordinal);

    // Memoizes "this space's .dm directory exists" so we skip the syscall on
    // the second-and-subsequent log per space. CreateDirectory is a no-op when
    // the dir already exists, but it's still a stat — at audit-log volumes the
    // accumulated cost is real, and the semantics are unchanged.
    private readonly ConcurrentDictionary<string, bool> _dirsCreated =
        new(StringComparer.Ordinal);

    private SemaphoreSlim GetLock(string space)
        => _locks.GetOrAdd(space, _ => new SemaphoreSlim(1, 1));

    public bool Enabled => !string.IsNullOrWhiteSpace(settings.Value.SpacesFolder);

    // Resolves the on-disk path for a space's audit log. Public so tests can
    // assert the layout without re-deriving it.
    public string ResolveLogPath(string space)
    {
        var root = settings.Value.SpacesFolder;
        return Path.Combine(root, space, ".dm", "events.jsonl");
    }

    public async Task LogAsync(Event e, CancellationToken ct = default)
    {
        if (!Enabled) return;
        if (string.IsNullOrEmpty(e.SpaceName)) return;

        // Python parity: action_log merges get_request_data() into attributes
        // before serializing — that's where `request_headers` comes from. We
        // do the same here, building a fresh attributes dict so we don't
        // mutate the caller's Event.Attribuztes (which plugins also see).
        var headers = CaptureRequestHeaders();
        var enriched = e;
        if (headers is not null)
        {
            var merged = new Dictionary<string, object>(e.Attributes, StringComparer.Ordinal)
            {
                ["request_headers"] = headers,
            };
            enriched = e with { Attributes = merged };
        }

        string line;
        try
        {
            line = SerializeLine(enriched);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "space-event-logger: failed to serialize event for {Space}/{Subpath}",
                e.SpaceName, e.Subpath);
            return;
        }

        var path = ResolveLogPath(e.SpaceName);
        var sem = GetLock(e.SpaceName);
        await sem.WaitAsync(ct);
        try
        {
            // Cache the "directory created" bit per space — typical hot path
            // is steady-state appends to an existing dir, so skip the stat.
            // TODO: add log rotation. The per-space events.jsonl grows
            // unbounded; matches Python upstream but is a real risk in
            // long-running deployments. Mitigation belongs in this writer
            // (e.g., size-based rollover to events.jsonl.<n>).
            if (!_dirsCreated.ContainsKey(e.SpaceName))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _dirsCreated[e.SpaceName] = true;
            }
            await File.AppendAllTextAsync(path, line, Encoding.UTF8, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "space-event-logger: failed to append to {Path}", path);
        }
        finally
        {
            sem.Release();
        }
    }

    // Builds the Action JSON line. Internal so the unit suite can assert the
    // exact field ordering / nesting without touching disk.
    internal static string SerializeLine(Event e)
    {
        // Python uses `datetime.now()` ISO format with 6-digit microseconds.
        // .NET's "ffffff" gives microseconds; "fffffff" would be 100ns ticks
        // (one digit too many for parity).
        var ts = TimeUtils.Now().ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

        var sb = new StringBuilder(256);
        sb.Append('{');

        // 1. resource (Locator) — Python's leading key.
        sb.Append("\"resource\":{");
        var first = true;
        AppendKv(sb, "uuid", e.Uuid, ref first);
        // Python always emits `domain` (currently always null in upstream).
        // Keep the key so byte-level diffs against Python logs only show
        // expected timestamp / uuid drift.
        AppendKvRaw(sb, "domain", "null", ref first);
        AppendKvRaw(sb, "type", SerializeResourceType(e.ResourceType), ref first);
        AppendKv(sb, "space_name", e.SpaceName, ref first);
        AppendKv(sb, "subpath", e.Subpath, ref first);
        AppendKv(sb, "shortname", e.Shortname, ref first);
        AppendKv(sb, "schema_shortname", e.SchemaShortname, ref first);
        AppendKvRaw(sb, "displayname", SerializeTranslation(e.Displayname), ref first);
        AppendKvRaw(sb, "description", SerializeTranslation(e.Description), ref first);
        AppendKvRaw(sb, "tags", SerializeTags(e.Tags), ref first);
        sb.Append('}');

        // 2. user_shortname
        sb.Append(",\"user_shortname\":");
        sb.Append(JsonEncode(e.UserShortname));

        // 3. request — action type as snake_case enum string.
        var action = JsonSerializer.Serialize(e.ActionType, DmartJsonContext.Default.ActionType);
        sb.Append(",\"request\":").Append(action);

        // 4. timestamp
        sb.Append(",\"timestamp\":\"").Append(ts).Append('"');

        // 5. attributes
        sb.Append(",\"attributes\":");
        sb.Append(SerializeAttributes(e.Attributes));

        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendKv(StringBuilder sb, string key, string? value, ref bool first)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(key).Append("\":");
        sb.Append(value is null ? "null" : JsonEncode(value));
    }

    private static void AppendKvRaw(StringBuilder sb, string key, string rawJson, ref bool first)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(key).Append("\":").Append(rawJson);
    }

    private static string SerializeResourceType(Dmart.Models.Enums.ResourceType? rt)
    {
        if (rt is null) return "null";
        return JsonSerializer.Serialize(rt.Value, DmartJsonContext.Default.ResourceType);
    }

    private static string SerializeTranslation(Translation? t)
    {
        if (t is null) return "null";
        return JsonSerializer.Serialize(t, DmartJsonContext.Default.Translation);
    }

    private static string SerializeTags(List<string>? tags)
    {
        if (tags is null) return "null";
        return JsonSerializer.Serialize(tags, DmartJsonContext.Default.ListString);
    }

    private static string SerializeAttributes(Dictionary<string, object> attrs)
    {
        // attrs may be empty, which Python serializes as {}. The source-gen
        // dictionary serializer handles nested JsonElement / scalar values
        // already registered in DmartJsonContext.
        return JsonSerializer.Serialize(attrs, DmartJsonContext.Default.DictionaryStringObject);
    }

    private static string JsonEncode(string s)
        => JsonSerializer.Serialize(s, DmartJsonContext.Default.String);

    // Snapshots the inbound HTTP request's headers, dropping the two Python
    // strips for credentials hygiene (cookie + authorization). Returns null
    // when there's no current request (CLI invocations, plugin internal
    // writes, tests) so the writer omits the request_headers key entirely
    // instead of emitting an empty object.
    private Dictionary<string, string>? CaptureRequestHeaders()
    {
        var ctx = httpContextAccessor?.HttpContext;
        if (ctx is null) return null;
        var src = ctx.Request.Headers;
        if (src.Count == 0) return null;

        var dict = new Dictionary<string, string>(src.Count, StringComparer.Ordinal);
        foreach (var kv in src)
        {
            if (_excludedHeaders.Contains(kv.Key)) continue;
            // Headers can be multi-valued; Python joins with ", " when
            // pydantic dumps them. Use the same join so values round-trip
            // through tooling that reads our log alongside Python's.
            dict[kv.Key] = kv.Value.ToString();
        }
        return dict;
    }
}
