using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
#if NET8_0_OR_GREATER
using Dmart.Client.Json;
#endif

namespace Dmart.Client;

// Typed CRUD facade matching the method surface of Dmart.SqlAdapter.
//
// Why a partial file: keeps the existing Response-shaped methods (QueryAsync,
// GetSpacesAsync, RetrieveEntryAsync, RequestAsync) untouched so current
// callers stay working. New typed methods land here so a downstream that
// could pick either backend (HTTP vs DB) sees the same method names and
// signatures.
//
// Parity rules:
//   - login/register/forget-password are HTTP-only — they live in DmartClient.cs
//     and DmartClient.Extra.cs and have no SqlAdapter equivalent.
//   - QueryAsync / GetSpacesAsync intentionally keep their Response-shaped
//     contract on Client to avoid breaking existing consumers; the typed
//     parity versions are exposed as QueryEntriesAsync / LoadSpacesAsync.
//   - The `actor` parameter exists for signature parity; on Client it's
//     ignored (the bearer token identifies the caller). Document downstream.
public sealed partial class DmartClient
{
    // -------------------------------------------------------------------
    // Entry CRUD — matches DmartSqlAdapter.LoadAsync / CreateAsync /
    // UpdateAsync / SaveAsync / DeleteAsync / MoveAsync / IsEntryExistAsync.
    // -------------------------------------------------------------------

    // Load a single entry. Returns null when missing. Mirrors
    // DmartSqlAdapter.LoadAsync(spaceName, subpath, shortname, resourceType?, ...).
    public async Task<Entry?> LoadAsync(
        string spaceName, string subpath, string shortname,
        ResourceType? resourceType = null,
        string? actor = null,
        CancellationToken ct = default)
    {
        var rt = resourceType ?? ResourceType.Content;
        var rtWire = ResourceTypeWire(rt);
        try
        {
            using var doc = await RetrieveEntryAsync(rtWire, spaceName, subpath, shortname,
                retrieveJsonPayload: true, retrieveAttachments: false,
                scope: "managed", ct).ConfigureAwait(false);
            return DeserializeEntry(doc);
        }
        catch (DmartException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Load via Locator. Same shape SqlAdapter exposes — handy for callers
    // that already have a Locator built (e.g. from a search result).
    public Task<Entry?> LoadOrNoneAsync(Locator locator, string? actor = null, CancellationToken ct = default)
        => LoadAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, actor, ct);

    // Existence probe — HEAD-equivalent via GET. Matches SqlAdapter.IsEntryExistAsync.
    public async Task<bool> IsEntryExistAsync(Locator locator, string? actor = null, CancellationToken ct = default)
    {
        var entry = await LoadOrNoneAsync(locator, actor, ct).ConfigureAwait(false);
        return entry is not null;
    }

    // Create — fails with DmartException if the entry already exists.
    // Server returns InternalErrorCode.RECORD_EXISTS on conflict.
    public Task<Response> CreateAsync(Entry entry, string? actor = null, CancellationToken ct = default)
        => RequestAsync(BuildEntryRequest(entry, RequestType.Create), ct);

    // Update — fails if the entry doesn't exist.
    public Task<Response> UpdateAsync(Entry entry, string? actor = null, CancellationToken ct = default)
        => RequestAsync(BuildEntryRequest(entry, RequestType.Update), ct);

    // Upsert (create-or-update). SqlAdapter calls this Save; we do the
    // existence probe + the right action so the wire envelope is honest
    // about whether this was a create or an update (which matters for the
    // server-side history-write path).
    public async Task<Response> SaveAsync(Entry entry, string? actor = null, CancellationToken ct = default)
    {
        var exists = await IsEntryExistAsync(
            new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname),
            actor, ct).ConfigureAwait(false);
        var action = exists ? RequestType.Update : RequestType.Create;
        return await RequestAsync(BuildEntryRequest(entry, action), ct).ConfigureAwait(false);
    }

    // Delete a single entry by locator. Matches SqlAdapter.DeleteAsync's
    // bool return: true on rows-deleted, false on "didn't exist".
    public async Task<bool> DeleteAsync(Locator locator, string? actor = null, CancellationToken ct = default)
    {
        var record = new Record
        {
            ResourceType = locator.Type,
            Shortname = locator.Shortname,
            Subpath = locator.Subpath,
        };
        var request = new Request
        {
            RequestType = RequestType.Delete,
            SpaceName = locator.SpaceName,
            Records = new() { record },
        };
        try
        {
            var resp = await RequestAsync(request, ct).ConfigureAwait(false);
            return resp.Status == Status.Success;
        }
        catch (DmartException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    // Move = rename or reparent. The dmart server's Request envelope for
    // move expects the new location in the record's `attributes.dest_*` keys.
    // Match SqlAdapter.MoveAsync's bool return.
    public async Task<bool> MoveAsync(Locator source, Locator target, string? actor = null, CancellationToken ct = default)
    {
        if (source.SpaceName != target.SpaceName)
        {
            // dmart's move is intra-space — cross-space requires
            // delete-then-create. Surface this clearly rather than silently
            // doing the wrong thing.
            throw new ArgumentException(
                "Move across spaces is not supported by the dmart server; " +
                "delete+create across spaces instead.",
                nameof(target));
        }
        var record = new Record
        {
            ResourceType = source.Type,
            Shortname = source.Shortname,
            Subpath = source.Subpath,
            Attributes = new Dictionary<string, object>
            {
                ["dest_subpath"] = target.Subpath,
                ["dest_shortname"] = target.Shortname,
            },
        };
        var request = new Request
        {
            RequestType = RequestType.Move,
            SpaceName = source.SpaceName,
            Records = new() { record },
        };
        try
        {
            var resp = await RequestAsync(request, ct).ConfigureAwait(false);
            return resp.Status == Status.Success;
        }
        catch (DmartException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------
    // UUID / slug lookups — wraps GET /managed/byuuid/{uuid} and
    // /managed/byslug/{slug}. IDOR-safe on the server side (it loads the
    // row then re-checks `view` against the resolved locator).
    // -------------------------------------------------------------------

    public async Task<Entry?> GetByUuidAsync(Guid uuid, string? actor = null, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Get,
                $"/managed/byuuid/{uuid.ToString("D", System.Globalization.CultureInfo.InvariantCulture)}");
            using var doc = await SendRawAsync(req, ct).ConfigureAwait(false);
            return DeserializeEntry(doc);
        }
        catch (DmartException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Entry?> GetBySlugAsync(string slug, string? actor = null, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Get,
                $"/managed/byslug/{Uri.EscapeDataString(slug)}");
            using var doc = await SendRawAsync(req, ct).ConfigureAwait(false);
            return DeserializeEntry(doc);
        }
        catch (DmartException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Generic criteria lookup — parity with DmartSqlAdapter.GetEntryByCriteriaAsync.
    // The DB version takes arbitrary column = value pairs; over HTTP we can
    // only express the criteria the server's Query / byuuid / byslug surfaces
    // accept. Supported keys: "uuid", "slug", and the Query-shape tuple
    // ("space_name", "subpath", "shortname"). Other keys are silently ignored;
    // if no key resolves to a callable lookup, returns null.
    public async Task<Entry?> GetEntryByCriteriaAsync(
        IReadOnlyDictionary<string, object?> criteria, string? actor = null,
        CancellationToken ct = default)
    {
        if (criteria.TryGetValue("uuid", out var uuidObj) && uuidObj is not null
            && Guid.TryParse(uuidObj.ToString(), out var u))
        {
            return await GetByUuidAsync(u, actor, ct).ConfigureAwait(false);
        }
        if (criteria.TryGetValue("slug", out var slugObj) && slugObj?.ToString() is { Length: > 0 } slug)
        {
            return await GetBySlugAsync(slug, actor, ct).ConfigureAwait(false);
        }
        if (!criteria.TryGetValue("shortname", out var snObj) || snObj?.ToString() is not { Length: > 0 } shortname)
        {
            return null;
        }
        var spaceName = criteria.TryGetValue("space_name", out var sn) ? sn?.ToString() ?? "" : "";
        var subpath = criteria.TryGetValue("subpath", out var sp) ? sp?.ToString() ?? "/" : "/";
        if (string.IsNullOrEmpty(spaceName)) return null;
        var (_, entries) = await QueryEntriesAsync(new Query
        {
            Type = QueryType.Search,
            SpaceName = spaceName,
            Subpath = subpath,
            Search = "",
            FilterShortnames = new() { shortname },
            Limit = 1,
        }, actor, ct: ct).ConfigureAwait(false);
        return entries.Count > 0 ? entries[0] : null;
    }

    // -------------------------------------------------------------------
    // Schema lookup — thin alias matching DmartSqlAdapter.GetSchemaAsync.
    // Schemas live under /schema with resource_type=schema.
    // -------------------------------------------------------------------

    public Task<Entry?> GetSchemaAsync(string spaceName, string shortname,
        string? actor = null, CancellationToken ct = default)
        => LoadAsync(spaceName, "/schema", shortname, ResourceType.Schema, actor, ct);

    // -------------------------------------------------------------------
    // History — wraps the server's /managed/query?type=history flow so the
    // Client surface matches DmartSqlAdapter.QueryHistoryAsync. The response
    // records are projected into HistoryRow shape consistent with the DB
    // adapter (uuid, space/subpath/shortname, timestamp, owner, headers, diff).
    // There is no /managed AppendHistory endpoint — the server auto-writes
    // histories on RequestHandler operations.
    // -------------------------------------------------------------------

    public sealed record HistoryRow
    {
        public required string Uuid { get; init; }
        public required string SpaceName { get; init; }
        public required string Subpath { get; init; }
        public required string Shortname { get; init; }
        public DateTime Timestamp { get; init; }
        public string? OwnerShortname { get; init; }
        public Dictionary<string, object>? RequestHeaders { get; init; }
        public Dictionary<string, object>? Diff { get; init; }
    }

    public async Task<List<HistoryRow>> QueryHistoryAsync(
        string spaceName, string subpath, string shortname,
        int limit = 50, string? actor = null, CancellationToken ct = default)
    {
        var resp = await QueryAsync(new Query
        {
            Type = QueryType.History,
            SpaceName = spaceName,
            Subpath = subpath,
            FilterShortnames = new() { shortname },
            Limit = limit,
        }, scope: "managed", ct).ConfigureAwait(false);
        var rows = new List<HistoryRow>(resp.Records?.Count ?? 0);
        if (resp.Records is null) return rows;
        foreach (var record in resp.Records)
        {
            var attrs = record.Attributes ?? new Dictionary<string, object>(StringComparer.Ordinal);
            rows.Add(new HistoryRow
            {
                Uuid = record.Uuid ?? string.Empty,
                SpaceName = spaceName,
                Subpath = record.Subpath ?? subpath,
                Shortname = record.Shortname,
                Timestamp = attrs.TryGetValue("timestamp", out var ts) ? CoerceDateTime(ts) : default,
                OwnerShortname = attrs.TryGetValue("owner_shortname", out var os) ? os?.ToString() : null,
                RequestHeaders = CoerceDict(attrs, "request_headers"),
                Diff = CoerceDict(attrs, "diff"),
            });
        }
        return rows;
    }

    // -------------------------------------------------------------------
    // Locks — parity-shape over LockEntryAsync/UnlockEntryAsync. The HTTP
    // server doesn't expose a lock-period knob (it's set server-side from
    // DmartSettings.LockPeriod), so the lockPeriodSeconds argument is
    // accepted purely for signature parity with DmartSqlAdapter.TryLockAsync
    // and ignored on the wire. ownerShortname is implicit on the bearer
    // token; we keep it in the signature so consumers can swap backends.
    // -------------------------------------------------------------------

    public async Task<bool> TryLockAsync(Locator locator, string ownerShortname,
        int lockPeriodSeconds = 300, CancellationToken ct = default)
    {
        _ = ownerShortname;
        _ = lockPeriodSeconds;
        try
        {
            var resp = await LockEntryAsync(locator.Type, locator.SpaceName,
                locator.Subpath, locator.Shortname, ct).ConfigureAwait(false);
            return resp.Status == Status.Success;
        }
        catch (DmartException ex) when (ex.StatusCode == 423)
        {
            // LOCKED_ENTRY / LOCK_UNAVAILABLE — someone else holds it.
            return false;
        }
    }

    public async Task<bool> UnlockAsync(Locator locator, string ownerShortname,
        CancellationToken ct = default)
    {
        _ = ownerShortname;
        try
        {
            var resp = await UnlockEntryAsync(locator.SpaceName,
                locator.Subpath, locator.Shortname, ct).ConfigureAwait(false);
            return resp.Status == Status.Success;
        }
        catch (DmartException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------
    // Spaces / users — typed parity with SqlAdapter.
    // -------------------------------------------------------------------

    // Load a single space by shortname. Matches SqlAdapter.FetchSpaceAsync.
    public async Task<Space?> FetchSpaceAsync(string spaceName, string? actor = null, CancellationToken ct = default)
    {
        try
        {
            using var doc = await RetrieveEntryAsync(
                "space", spaceName, "/", spaceName,
                retrieveJsonPayload: true, retrieveAttachments: false,
                scope: "managed", ct).ConfigureAwait(false);
            return DeserializeSpace(doc);
        }
        catch (DmartException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Typed companion to GetSpacesAsync — returns the full spaces dictionary
    // keyed by shortname, matching SqlAdapter.GetSpacesAsync. The existing
    // GetSpacesAsync still returns the Response envelope for back-compat.
    public async Task<Dictionary<string, Space>> LoadSpacesAsync(string? actor = null, CancellationToken ct = default)
    {
        var resp = await GetSpacesAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, Space>(StringComparer.Ordinal);
        if (resp.Records is null) return result;
        foreach (var record in resp.Records)
        {
            var space = DeserializeSpaceFromRecord(record);
            if (space is not null) result[space.Shortname] = space;
        }
        return result;
    }

    // Load a user record. Matches SqlAdapter.LoadUserMetaAsync.
    public async Task<User?> LoadUserMetaAsync(string shortname, string? actor = null, CancellationToken ct = default)
    {
        try
        {
            using var doc = await RetrieveEntryAsync(
                "user", "management", "/users", shortname,
                retrieveJsonPayload: false, retrieveAttachments: false,
                scope: "managed", ct).ConfigureAwait(false);
            return DeserializeUser(doc);
        }
        catch (DmartException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // -------------------------------------------------------------------
    // Query — typed companion to QueryAsync.
    //
    // Returns (Total, Records) to match SqlAdapter.QueryAsync's return shape.
    // Inherits the existing QueryAsync transport: server envelope, bearer
    // token, scope selection.
    // -------------------------------------------------------------------

    public async Task<(int Total, List<Entry> Records)> QueryEntriesAsync(
        Query query, string? actor = null, string scope = "managed",
        CancellationToken ct = default)
    {
        var resp = await QueryAsync(query, scope, ct).ConfigureAwait(false);
        var entries = new List<Entry>(resp.Records?.Count ?? 0);
        if (resp.Records is not null)
        {
            foreach (var record in resp.Records)
            {
                var entry = DeserializeEntryFromRecord(record);
                if (entry is not null) entries.Add(entry);
            }
        }
        var total = resp.AttributesTotal();
        return (total, entries);
    }

    // -------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------

    private static Request BuildEntryRequest(Entry entry, RequestType action)
    {
        var record = EntryToRecord(entry);
        return new Request
        {
            RequestType = action,
            SpaceName = entry.SpaceName,
            Records = new() { record },
        };
    }

    // Project an Entry into a wire Record. Mirrors the shape dmart's server
    // produces in /managed/query responses, so a round-trip through Save +
    // Load reproduces the same fields. Reads that need the JSON payload come
    // back via Entry.Payload.Body which we project into attributes.
    private static Record EntryToRecord(Entry entry)
    {
        var attrs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["is_active"] = entry.IsActive,
            ["tags"] = entry.Tags ?? new(),
            ["owner_shortname"] = entry.OwnerShortname,
        };
        if (entry.OwnerGroupShortname is not null) attrs["owner_group_shortname"] = entry.OwnerGroupShortname;
        if (entry.Displayname is not null) attrs["displayname"] = entry.Displayname;
        if (entry.Description is not null) attrs["description"] = entry.Description;
        if (entry.Slug is not null) attrs["slug"] = entry.Slug;
        if (entry.Acl is not null) attrs["acl"] = entry.Acl;
        if (entry.Relationships is not null) attrs["relationships"] = entry.Relationships;
        if (entry.State is not null) attrs["state"] = entry.State;
        if (entry.IsOpen is not null) attrs["is_open"] = entry.IsOpen;
        if (entry.WorkflowShortname is not null) attrs["workflow_shortname"] = entry.WorkflowShortname;
        if (entry.Collaborators is not null) attrs["collaborators"] = entry.Collaborators;
        if (entry.ResolutionReason is not null) attrs["resolution_reason"] = entry.ResolutionReason;
        if (entry.Payload is not null) attrs["payload"] = entry.Payload;

        return new Record
        {
            ResourceType = entry.ResourceType,
            Shortname = entry.Shortname,
            Subpath = entry.Subpath,
            Uuid = entry.Uuid,
            Attributes = attrs,
        };
    }

    // The RetrieveEntry endpoint returns a flat Meta-shaped object directly
    // (not the Response envelope) — see DmartClient.RetrieveEntryAsync. We
    // hand it the same JSON options used elsewhere so snake_case
    // round-trips correctly.
    private static Entry? DeserializeEntry(JsonDocument doc)
    {
        var raw = doc.RootElement.GetRawText();
#if NET8_0_OR_GREATER
        return JsonSerializer.Deserialize(raw, DmartClientJsonContext.Default.Entry);
#else
        return JsonSerializer.Deserialize<Entry>(raw, DefaultJsonOptions);
#endif
    }

    private static Space? DeserializeSpace(JsonDocument doc)
    {
        var raw = doc.RootElement.GetRawText();
#if NET8_0_OR_GREATER
        return JsonSerializer.Deserialize(raw, DmartClientJsonContext.Default.Space);
#else
        return JsonSerializer.Deserialize<Space>(raw, DefaultJsonOptions);
#endif
    }

    private static User? DeserializeUser(JsonDocument doc)
    {
        var raw = doc.RootElement.GetRawText();
#if NET8_0_OR_GREATER
        return JsonSerializer.Deserialize(raw, DmartClientJsonContext.Default.User);
#else
        return JsonSerializer.Deserialize<User>(raw, DefaultJsonOptions);
#endif
    }

    // Convert a query-response Record back into an Entry. The server stamps
    // record.attributes with the Entry's column values; we re-serialize and
    // deserialize through Entry's shape so the field mapping stays in one
    // place (the JSON converters / snake-case policy).
    private static Entry? DeserializeEntryFromRecord(Record record)
    {
        if (record.Attributes is null) return null;
        // Merge unique base + uuid + attributes into a single JSON object so
        // STJ can hydrate an Entry from it.
        var merged = new Dictionary<string, object>(record.Attributes, StringComparer.Ordinal)
        {
            ["resource_type"] = record.ResourceType,
            ["shortname"] = record.Shortname,
            ["subpath"] = record.Subpath,
        };
        if (record.Uuid is not null) merged["uuid"] = record.Uuid;
#if NET8_0_OR_GREATER
        var json = JsonSerializer.Serialize(merged, DmartClientJsonContext.Default.DictionaryStringObject);
        return JsonSerializer.Deserialize(json, DmartClientJsonContext.Default.Entry);
#else
        var json = JsonSerializer.Serialize(merged, DefaultJsonOptions);
        return JsonSerializer.Deserialize<Entry>(json, DefaultJsonOptions);
#endif
    }

    private static Space? DeserializeSpaceFromRecord(Record record)
    {
        if (record.Attributes is null) return null;
        var merged = new Dictionary<string, object>(record.Attributes, StringComparer.Ordinal)
        {
            ["resource_type"] = record.ResourceType,
            ["shortname"] = record.Shortname,
            ["subpath"] = record.Subpath,
            ["space_name"] = record.Shortname,
        };
        if (record.Uuid is not null) merged["uuid"] = record.Uuid;
#if NET8_0_OR_GREATER
        var json = JsonSerializer.Serialize(merged, DmartClientJsonContext.Default.DictionaryStringObject);
        return JsonSerializer.Deserialize(json, DmartClientJsonContext.Default.Space);
#else
        var json = JsonSerializer.Serialize(merged, DefaultJsonOptions);
        return JsonSerializer.Deserialize<Space>(json, DefaultJsonOptions);
#endif
    }

    private static string ResourceTypeWire(ResourceType rt)
    {
#if NET8_0_OR_GREATER
        return JsonSerializer.Serialize(rt, DmartClientJsonContext.Default.ResourceType).Trim('"');
#else
        return JsonSerializer.Serialize(rt, DefaultJsonOptions).Trim('"');
#endif
    }

    // History payloads come through Records.Attributes as raw JSON elements;
    // unbox them into the typed shapes the DB adapter exposes so callers see
    // the same value types regardless of backend.
    private static DateTime CoerceDateTime(object? value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        string s when DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) => parsed,
        JsonElement el when el.ValueKind == JsonValueKind.String && el.TryGetDateTime(out var v) => v,
        _ => default,
    };

    private static Dictionary<string, object>? CoerceDict(IDictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var raw) || raw is null) return null;
        if (raw is Dictionary<string, object> already) return already;
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
#if NET8_0_OR_GREATER
            return JsonSerializer.Deserialize(el.GetRawText(), DmartClientJsonContext.Default.DictionaryStringObject);
#else
            return JsonSerializer.Deserialize<Dictionary<string, object>>(el.GetRawText(), DefaultJsonOptions);
#endif
        }
        return null;
    }
}

// Small extension to extract pagination total from the envelope's
// attributes bag. Kept as an extension method so the partial class stays
// tidy.
internal static class ResponseExtensions
{
    public static int AttributesTotal(this Response resp)
    {
        if (resp.Attributes is null) return 0;
        if (!resp.Attributes.TryGetValue("total", out var raw)) return 0;
        return raw switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var v) => v,
            JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) => (int)v,
            _ => 0,
        };
    }
}
