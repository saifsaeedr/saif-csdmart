using System.Globalization;
using System.Text;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.SqlAdapter.Helpers;
using Dmart.SqlAdapter.Permissions;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.SqlAdapter;

// Direct PostgreSQL access to a dmart-managed database.
//
// This is the C# equivalent of dmart Python's `data_adapters/sql_adapter.py`.
// The method surface deliberately mirrors the Python adapter (Load, Save,
// Create, Update, Delete, Move, Query, IsEntryExist, FetchSpace, GetSpaces,
// LoadUserMeta, GetUserPermissions, InitializeSpaces) so a Python developer
// reading dmart's sources finds the same names with the same semantics.
//
// RBAC enforcement
// ----------------
// When the adapter is constructed with a PermissionEngine, every public
// method takes an `actor` shortname and is gated by the same role / ACL /
// query-policy contract that dmart's HTTP API enforces. Methods throw
// DmartPermissionDeniedException on a deny outcome — except QueryAsync,
// which silently filters unauthorized rows out (matching the API: the
// caller sees an empty page rather than an exception).
//
// When the adapter is constructed WITHOUT an engine, no RBAC is applied —
// "system context" mode for ETL, migrations, and other trusted callers.
// An explicit toggle, not an accident: the constructor that omits the
// engine is plainly different from the one that takes it.
//
// What it is NOT
// --------------
//   - AOT-friendly. JSONB serialization uses reflection-based STJ. Consumers
//     that need AOT should supply their own JsonSerializerOptions with a
//     source-gen JsonTypeInfoResolver.
//   - A schema validator. JSON Schema enforcement, workflow transitions,
//     and plugin dispatch live in the dmart server — calls that need those
//     should still go through the HTTP API.
public sealed partial class DmartSqlAdapter
{
    private readonly DmartDb _db;
    private readonly JsonSerializerOptions _json;
    private readonly PermissionEngine? _engine;
    private readonly DmartSqlAdapterOptions _options;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Expiry, Dictionary<string, object?>? Perms)> _userPermissionsCache
        = new(StringComparer.Ordinal);
    private static readonly TimeSpan UserPermissionsCacheTtl = TimeSpan.FromMinutes(5);

    public DmartSqlAdapter(DmartDb db, JsonSerializerOptions? jsonOptions = null,
        PermissionEngine? engine = null, DmartSqlAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _options = options ?? new DmartSqlAdapterOptions();
        _json = jsonOptions ?? _options.JsonOptions ?? DefaultJsonOptions();
        _engine = engine;
    }

    public DmartSqlAdapter(string connectionString)
        : this(new DmartDb(connectionString)) { }

    // Convenience constructor: builds the engine off the same Db so callers
    // don't have to wire it themselves. RBAC is ON.
    public static DmartSqlAdapter WithRbac(DmartDb db, JsonSerializerOptions? jsonOptions = null,
        DmartSqlAdapterOptions? options = null)
    {
        var opts = options ?? new DmartSqlAdapterOptions();
        var json = jsonOptions ?? opts.JsonOptions ?? DefaultJsonOptions();
        var engine = new PermissionEngine(db, json);
        return new DmartSqlAdapter(db, json, engine, opts);
    }

    private static JsonSerializerOptions DefaultJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public DmartDb Db => _db;
    public PermissionEngine? Engine => _engine;
    public bool RbacEnabled => _engine is not null;
    public DmartSqlAdapterOptions Options => _options;

    // ---------------------------------------------------------------------
    // Entry: load / save / update / create / delete / move
    // ---------------------------------------------------------------------

    private const string EntryColumns = """
        uuid, shortname, space_name, subpath, is_active, slug,
        displayname, description, tags, created_at, updated_at,
        owner_shortname, owner_group_shortname, acl, payload, relationships,
        last_checksum_history, resource_type,
        state, is_open, workflow_shortname, collaborators,
        resolution_reason, query_policies
        """;

    public async Task<Entry?> LoadAsync(string spaceName, string subpath, string shortname,
        ResourceType? resourceType = null, string? actor = null, CancellationToken ct = default)
    {
        subpath = Locator.NormalizeSubpath(subpath);
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {EntryColumns} FROM entries WHERE space_name=$1 AND subpath=$2 AND shortname=$3";
        if (resourceType.HasValue) sql += " AND resource_type=$4";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        if (resourceType.HasValue) cmd.Parameters.Add(new() { Value = EnumWire(resourceType.Value) });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Python fallback: when type filter misses, retry without it. The
            // entries UNIQUE constraint is (shortname, space_name, subpath),
            // so resource_type is redundant for uniqueness.
            if (!resourceType.HasValue) return null;
            return await LoadAsync(spaceName, subpath, shortname, null, actor, ct).ConfigureAwait(false);
        }
        var entry = HydrateEntry(reader);

        // RBAC: after loading the row, check `view` with the resource context
        // so own/is_active conditions resolve correctly. Mirrors the dmart
        // API handler — load → check → return.
        if (_engine is not null)
        {
            await _engine.RequireAsync(actor,
                "view",
                new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname),
                ResourceContext.FromEntry(entry), null, ct).ConfigureAwait(false);
        }
        return entry;
    }

    public Task<Entry?> LoadOrNoneAsync(Locator locator, string? actor = null, CancellationToken ct = default)
        => LoadAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type, actor, ct);

    public async Task<Entry?> GetEntryByCriteriaAsync(IReadOnlyDictionary<string, object?> criteria,
        string? actor = null, CancellationToken ct = default)
    {
        if (criteria.Count == 0) return null;
        var sb = new StringBuilder($"SELECT {EntryColumns} FROM entries WHERE ");
        var parts = new List<string>();
        var values = new List<object?>();
        var i = 1;
        foreach (var kv in criteria)
        {
            parts.Add($"{Quote(kv.Key)} = ${i.ToString(CultureInfo.InvariantCulture)}");
            values.Add(kv.Value);
            i++;
        }
        sb.Append(string.Join(" AND ", parts));
        sb.Append(" LIMIT 1");

        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sb.ToString(), conn);
        foreach (var v in values) cmd.Parameters.Add(new() { Value = v ?? DBNull.Value });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var entry = HydrateEntry(reader);

        // RBAC: drop the row if the actor can't view it. Matches QueryAsync's
        // "filter, don't throw" behavior — criteria lookups are search-like.
        if (_engine is not null)
        {
            var allowed = await _engine.CanAsync(actor,
                "view",
                new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname),
                ResourceContext.FromEntry(entry), null, ct).ConfigureAwait(false);
            if (!allowed) return null;
        }
        return entry;
    }

    public async Task SaveAsync(Entry entry, string? actor = null, CancellationToken ct = default)
    {
        // Single round-trip: load the (possibly missing) row once, then RBAC-gate
        // as "update" when present or "create" when absent. Avoids the EXISTS+LOAD
        // double-select the early version did.
        var existing = await LoadRawAsync(entry.SpaceName, entry.Subpath, entry.Shortname, ct).ConfigureAwait(false);
        if (_engine is not null)
        {
            var action = existing is not null ? "update" : "create";
            var ctx = existing is not null ? ResourceContext.FromEntry(existing) : null;
            await _engine.RequireAsync(actor, action, LocatorFor(entry), ctx, null, ct).ConfigureAwait(false);
        }
        await UpsertEntryInternalAsync(entry, ct).ConfigureAwait(false);
        InvalidateRbacCacheFor(entry.ResourceType, entry.Shortname);
    }

    public async Task CreateAsync(Entry entry, string? actor = null, CancellationToken ct = default)
    {
        // One LoadRaw covers both the conflict check and the RBAC `create`
        // gate (which doesn't need a context but does need to see the row
        // is absent before issuing the upsert).
        var existing = await LoadRawAsync(entry.SpaceName, entry.Subpath, entry.Shortname, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Entry already exists: {entry.SpaceName}{entry.Subpath}/{entry.Shortname}");
        }
        if (_engine is not null)
            await _engine.RequireAsync(actor, "create", LocatorFor(entry), null, null, ct).ConfigureAwait(false);
        await UpsertEntryInternalAsync(entry, ct).ConfigureAwait(false);
        InvalidateRbacCacheFor(entry.ResourceType, entry.Shortname);
    }

    public async Task UpdateAsync(Entry entry, string? actor = null, CancellationToken ct = default)
    {
        var existing = await LoadRawAsync(entry.SpaceName, entry.Subpath, entry.Shortname, ct).ConfigureAwait(false);
        if (existing is null)
        {
            throw new InvalidOperationException(
                $"Entry does not exist: {entry.SpaceName}{entry.Subpath}/{entry.Shortname}");
        }
        if (_engine is not null)
            await _engine.RequireAsync(actor, "update", LocatorFor(entry),
                ResourceContext.FromEntry(existing), null, ct).ConfigureAwait(false);
        await UpsertEntryInternalAsync(entry, ct).ConfigureAwait(false);
        InvalidateRbacCacheFor(entry.ResourceType, entry.Shortname);
    }

    public async Task<bool> DeleteAsync(Locator locator, string? actor = null, CancellationToken ct = default)
    {
        // Always load — we need the row both for the RBAC context (when
        // RBAC is on) and to know the resource_type for cache invalidation
        // after a successful delete.
        var existing = await LoadRawAsync(locator.SpaceName, locator.Subpath, locator.Shortname, ct).ConfigureAwait(false);
        if (existing is null) return false;
        if (_engine is not null)
        {
            await _engine.RequireAsync(actor, "delete", locator,
                ResourceContext.FromEntry(existing), null, ct).ConfigureAwait(false);
        }

        var subpath = Locator.NormalizeSubpath(locator.Subpath);
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM entries WHERE space_name=$1 AND subpath=$2 AND shortname=$3 AND resource_type=$4",
            conn);
        cmd.Parameters.Add(new() { Value = locator.SpaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = locator.Shortname });
        cmd.Parameters.Add(new() { Value = EnumWire(locator.Type) });
        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows > 0) InvalidateRbacCacheFor(existing.ResourceType, existing.Shortname);
        return rows > 0;
    }

    public async Task<bool> MoveAsync(Locator source, Locator target,
        string? actor = null, CancellationToken ct = default)
    {
        // One load covers both RBAC source-context and the post-move
        // cache-invalidation key (when moving a User/Role/Permission row).
        var existing = await LoadRawAsync(source.SpaceName, source.Subpath, source.Shortname, ct).ConfigureAwait(false);
        if (existing is null) return false;
        if (_engine is not null)
        {
            // Move = delete-at-source + create-at-target, matching the dmart
            // API's permission requirement for /managed/request type=move.
            var srcCtx = ResourceContext.FromEntry(existing);
            await _engine.RequireAsync(actor, "delete", source, srcCtx, null, ct).ConfigureAwait(false);
            await _engine.RequireAsync(actor, "create", target, null, null, ct).ConfigureAwait(false);
        }

        var srcSubpath = Locator.NormalizeSubpath(source.Subpath);
        var tgtSubpath = Locator.NormalizeSubpath(target.Subpath);
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            UPDATE entries
               SET space_name = $4, subpath = $5, shortname = $6, updated_at = NOW()
             WHERE space_name = $1 AND subpath = $2 AND shortname = $3
            """, conn);
        cmd.Parameters.Add(new() { Value = source.SpaceName });
        cmd.Parameters.Add(new() { Value = srcSubpath });
        cmd.Parameters.Add(new() { Value = source.Shortname });
        cmd.Parameters.Add(new() { Value = target.SpaceName });
        cmd.Parameters.Add(new() { Value = tgtSubpath });
        cmd.Parameters.Add(new() { Value = target.Shortname });
        int rows;
        try
        {
            rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // unique_violation on (shortname, space_name, subpath) — target
            // already occupied. Translate to a typed exception that mirrors
            // CreateAsync's "already exists" failure shape.
            throw new InvalidOperationException(
                $"Move target already exists: {target.SpaceName}{target.Subpath}/{target.Shortname}", ex);
        }
        if (rows > 0)
        {
            // Invalidate the source key (the row's old shortname). The target
            // shortname starts with no cache entry — nothing to invalidate.
            InvalidateRbacCacheFor(existing.ResourceType, existing.Shortname);
        }
        return rows > 0;
    }

    public async Task<bool> IsEntryExistAsync(Locator locator, string? actor = null, CancellationToken ct = default)
    {
        // Existence is treated as a "view" — if the actor can't see the row,
        // they shouldn't be able to probe its existence either. One LoadRaw
        // serves both the existence check and the RBAC view-context check.
        var existing = await LoadRawAsync(locator.SpaceName, locator.Subpath, locator.Shortname, ct).ConfigureAwait(false);
        if (existing is null) return false;
        if (_engine is null) return true;
        return await _engine.CanAsync(actor, "view", locator,
            ResourceContext.FromEntry(existing), null, ct).ConfigureAwait(false);
    }

    // Mirrors the server's GET /managed/byuuid/{uuid}. The URL doesn't carry a
    // space/subpath so we resolve the row first, then apply the same `view`
    // gate that LoadAsync uses with the resolved Locator. Returns null on a
    // permission deny (matches QueryAsync's "filter, don't throw" — UUID
    // lookups are search-shaped, not address-shaped).
    public async Task<Entry?> GetByUuidAsync(Guid uuid, string? actor = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {EntryColumns} FROM entries WHERE uuid=$1", conn);
        cmd.Parameters.Add(new() { Value = uuid });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var entry = HydrateEntry(reader);
        if (_engine is null) return entry;
        var allowed = await _engine.CanAsync(actor, "view",
            new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname),
            ResourceContext.FromEntry(entry), null, ct).ConfigureAwait(false);
        return allowed ? entry : null;
    }

    // Mirrors GET /managed/byslug/{slug}. Same IDOR-safe gate as GetByUuidAsync.
    public async Task<Entry?> GetBySlugAsync(string slug, string? actor = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {EntryColumns} FROM entries WHERE slug=$1", conn);
        cmd.Parameters.Add(new() { Value = slug });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var entry = HydrateEntry(reader);
        if (_engine is null) return entry;
        var allowed = await _engine.CanAsync(actor, "view",
            new Locator(entry.ResourceType, entry.SpaceName, entry.Subpath, entry.Shortname),
            ResourceContext.FromEntry(entry), null, ct).ConfigureAwait(false);
        return allowed ? entry : null;
    }

    // ----- Raw helpers (bypass RBAC; for internal pre/post checks only) -----

    // Invalidate cached permissions when a write touches a User/Role/Permission
    // row. Per-user invalidation when a User row changes; full flush when a
    // Role or Permission row changes (graph-wide downstream impact).
    private void InvalidateRbacCacheFor(ResourceType type, string shortname)
    {
        if (_engine is null) return;
        switch (type)
        {
            case ResourceType.User:
                _engine.Invalidate(shortname);
                _userPermissionsCache.TryRemove(shortname, out _);
                break;
            case ResourceType.Role:
            case ResourceType.Permission:
                _engine.InvalidateAll();
                _userPermissionsCache.Clear();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Drop the cached merged-permissions view for a user, or all users when
    /// <paramref name="shortname"/> is null. Call this if you've edited
    /// permission rows outside this adapter (raw SQL, server-side admin).
    /// </summary>
    public void InvalidateUserPermissionsCache(string? shortname = null)
    {
        if (shortname is null) _userPermissionsCache.Clear();
        else _userPermissionsCache.TryRemove(shortname, out _);
    }

    private async Task<Entry?> LoadRawAsync(string spaceName, string subpath, string shortname, CancellationToken ct)
    {
        subpath = Locator.NormalizeSubpath(subpath);
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {EntryColumns} FROM entries WHERE space_name=$1 AND subpath=$2 AND shortname=$3", conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? HydrateEntry(reader) : null;
    }

    private static Locator LocatorFor(Entry e)
        => new(e.ResourceType, e.SpaceName, e.Subpath, e.Shortname);

    // ---------------------------------------------------------------------
    // Query (paged search over entries)
    // ---------------------------------------------------------------------

    public async Task<(int Total, List<Entry> Records)> QueryAsync(Query query,
        string? actor = null, CancellationToken ct = default)
    {
        // RBAC: build the actor's user-query-policy list. Empty list ⇒ no
        // visibility ⇒ short-circuit, matching Python's `if not sql_query_policies:
        // return (0, [])`. The engine being null means RBAC is OFF and the
        // policy list isn't applied — every row is candidate.
        List<string>? policies = null;
        if (_engine is not null)
        {
            policies = await _engine.BuildUserQueryPoliciesAsync(
                actor, query.SpaceName, query.Subpath, ct).ConfigureAwait(false);
            if (policies.Count == 0) return (0, new List<Entry>());
        }

        var subpath = Locator.NormalizeSubpath(query.Subpath);
        var where = new List<string> { "space_name = @space" };
        var pars = new List<NpgsqlParameter> { new("@space", query.SpaceName) };

        // Subpath: ExactSubpath=true → equality match only. Otherwise the
        // previous hierarchical match (self + children). Mirrors
        // QueryHelper.BuildWhereClause:36-45.
        if (query.ExactSubpath)
        {
            where.Add("subpath = @subpath");
            pars.Add(new("@subpath", subpath));
        }
        else if (subpath != "/")
        {
            where.Add("(subpath = @subpath OR subpath LIKE @subpath_prefix)");
            pars.Add(new("@subpath", subpath));
            pars.Add(new("@subpath_prefix", subpath + "/%"));
        }

        if (query.FilterTypes is { Count: > 0 })
        {
            var ph = string.Join(",", query.FilterTypes.Select((_, idx) => $"@rt{idx}"));
            where.Add($"resource_type IN ({ph})");
            for (var i = 0; i < query.FilterTypes.Count; i++)
                pars.Add(new($"@rt{i}", EnumWire(query.FilterTypes[i])));
        }

        if (query.FilterShortnames is { Count: > 0 })
        {
            var ph = string.Join(",", query.FilterShortnames.Select((_, idx) => $"@sn{idx}"));
            where.Add($"shortname IN ({ph})");
            for (var i = 0; i < query.FilterShortnames.Count; i++)
                pars.Add(new($"@sn{i}", query.FilterShortnames[i]));
        }

        // Schema filter: Dmart.Models.Query exposes only the plural form,
        // and Python-parity defaults it to {"meta"} to exclude meta entries.
        // Treat a single "meta" entry as the "no filter" default — callers
        // who want meta-only or a real filter pass a different list. Mirrors
        // QueryHelper.BuildWhereClause:67-79.
        if (query.FilterSchemaNames is { Count: > 0 }
            && !(query.FilterSchemaNames.Count == 1 && query.FilterSchemaNames[0] == "meta"))
        {
            var ph = string.Join(",", query.FilterSchemaNames.Select((_, idx) => $"@schema{idx}"));
            where.Add($"payload->>'schema_shortname' IN ({ph})");
            for (var i = 0; i < query.FilterSchemaNames.Count; i++)
                pars.Add(new($"@schema{i}", query.FilterSchemaNames[i]));
        }

        // Tag filter: jsonb array overlap (`?|` operator). Mirrors
        // QueryHelper.BuildWhereClause:81-89.
        if (query.FilterTags is { Count: > 0 })
        {
            where.Add("tags ?| @tags");
            pars.Add(new("@tags", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = query.FilterTags.ToArray(),
            });
        }

        if (query.FromDate.HasValue) { where.Add("created_at >= @from"); pars.Add(new("@from", query.FromDate.Value)); }
        if (query.ToDate.HasValue)   { where.Add("created_at <= @to");   pars.Add(new("@to", query.ToDate.Value)); }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Full port of csdmart's RediSearch-style search expression:
            // selectors (`@field:v`), negation, alternations, ranges,
            // comparisons, payload paths (incl. arrays), and plain-word
            // free text. See Helpers/SearchExpressionParser.cs for the
            // grammar and per-shape SQL emission.
            var parsed = Helpers.SearchExpressionParser.Parse(query.Search, pars.Count);
            foreach (var clause in parsed.Clauses) where.Add(clause);
            foreach (var p in parsed.Parameters) pars.Add(p);
        }

        var whereClause = new StringBuilder(string.Join(" AND ", where));

        // RBAC: append the per-row visibility clause. Stays a no-op when
        // _engine is null OR actor is null — same short-circuit as the
        // server's QueryHelper.AppendAclFilter.
        if (_engine is not null && actor is not null)
        {
            PermissionFilter.Append(whereClause, pars, actor, "entries", policies);
        }

        var whereClauseStr = whereClause.ToString();

        // Total first (lets the caller render pagination without re-issuing
        // the query). Callers that don't need a total can set
        // query.RetrieveTotal=false — useful for endless-scroll UIs that
        // would otherwise pay for an unnecessary COUNT(*) per page. Null
        // → defaults to true (existing behavior); only an explicit false
        // skips. Mirrors csdmart Query.RetrieveTotal semantics.
        var total = 0;
        if (query.RetrieveTotal is not false)
        {
            await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM entries WHERE {whereClauseStr}", conn);
            foreach (var p in pars) cmd.Parameters.Add(Clone(p));
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            total = raw is long l ? (int)l : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }

        var records = new List<Entry>(query.Limit);
        {
            var orderBy = string.IsNullOrEmpty(query.SortBy) ? "created_at" : Quote(query.SortBy!);
            var orderDir = query.SortType == Dmart.Models.Enums.SortType.Ascending ? "ASC" : "DESC";
            var sql = $"""
                SELECT {EntryColumns} FROM entries
                 WHERE {whereClauseStr}
                 ORDER BY {orderBy} {orderDir}
                 LIMIT @limit OFFSET @offset
                """;
            await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var p in pars) cmd.Parameters.Add(Clone(p));
            cmd.Parameters.Add(new("@limit", query.Limit));
            cmd.Parameters.Add(new("@offset", query.Offset));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                records.Add(HydrateEntry(reader));
        }

        return (total, records);
    }

    // Mirrors tsdmart's getChildren / Client.GetChildrenAsync. Lists direct
    // children of a (space, subpath) using ExactSubpath=true so descendants
    // of nested folders aren't included. Pure convenience over QueryAsync —
    // same RBAC filter applies.
    public Task<(int Total, List<Entry> Records)> GetChildrenAsync(
        string spaceName, string subpath,
        string search = "", int limit = 20, int offset = 0,
        IReadOnlyList<ResourceType>? restrictTypes = null,
        string? actor = null, CancellationToken ct = default)
        => QueryAsync(new Query
        {
            Type = QueryType.Search,
            SpaceName = spaceName,
            Subpath = subpath,
            FilterTypes = restrictTypes?.ToList(),
            ExactSubpath = true,
            Search = search,
            Limit = limit,
            Offset = offset,
        }, actor, ct);

    // ---------------------------------------------------------------------
    // Space
    // ---------------------------------------------------------------------

    private const string SpaceColumns = """
        uuid, shortname, is_active, displayname, description,
        created_at, updated_at, owner_shortname, owner_group_shortname, acl,
        root_registration_signature, active_plugins, languages,
        capture_misses, check_health
        """;

    public async Task<Space?> FetchSpaceAsync(string spaceName, string? actor = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SpaceColumns} FROM spaces WHERE shortname=$1",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var space = HydrateSpace(reader);

        if (_engine is not null)
        {
            // Spaces use space_name = shortname for the permission key.
            await _engine.RequireAsync(actor,
                "view",
                new Locator(ResourceType.Space, space.Shortname, "/", space.Shortname),
                ResourceContext.FromSpace(space), null, ct).ConfigureAwait(false);
        }
        return space;
    }

    public async Task<Dictionary<string, Space>> GetSpacesAsync(string? actor = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, Space>(StringComparer.Ordinal);
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SpaceColumns} FROM spaces ORDER BY shortname",
            conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var all = new List<Space>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) all.Add(HydrateSpace(reader));

        if (_engine is null)
        {
            foreach (var s in all) result[s.Shortname] = s;
            return result;
        }

        // RBAC: drop spaces the actor can't view. Per-space check keeps fidelity
        // with the server's spaces listing (HasAnyAccessToSpaceAsync).
        foreach (var s in all)
        {
            var allowed = await _engine.CanAsync(actor,
                "view",
                new Locator(ResourceType.Space, s.Shortname, "/", s.Shortname),
                ResourceContext.FromSpace(s), null, ct).ConfigureAwait(false);
            if (allowed) result[s.Shortname] = s;
        }
        return result;
    }

    // ---------------------------------------------------------------------
    // User
    // ---------------------------------------------------------------------

    private const string UserColumns = """
        uuid, shortname, space_name, subpath, is_active, displayname, description,
        created_at, updated_at, owner_shortname, owner_group_shortname, acl,
        password, roles, groups, type, language, email, msisdn,
        is_email_verified, is_msisdn_verified, force_password_change,
        google_id, facebook_id, attempt_count, query_policies
        """;

    // Mirrors tsdmart's getProfile (GET /user/profile) — read the calling
    // actor's own user record. The HTTP server resolves "self" from the
    // bearer token; the DB adapter has no token, so the actor IS the
    // shortname. Throws on `actor is null` because there's no profile to
    // return for an anonymous caller (HTTP returns 401 in that case).
    //
    // GetProfileAsync uses the auth-shaped loader so the caller can read
    // their own password hash for credential-flow scenarios (rotate, verify).
    public Task<User?> GetProfileAsync(string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actor))
            throw new ArgumentException("actor (current user shortname) is required", nameof(actor));
        return LoadUserMetaForAuthAsync(actor, actor, ct);
    }

    /// <summary>
    /// Load a user record with the password hash REDACTED. The returned
    /// <see cref="User.Password"/> is always null. Use this for read-side
    /// flows that surface user records to the application layer.
    /// </summary>
    public async Task<User?> LoadUserMetaAsync(string shortname, string? actor = null, CancellationToken ct = default)
    {
        var user = await LoadUserMetaForAuthAsync(shortname, actor, ct).ConfigureAwait(false);
        if (user is null) return null;
        return user with { Password = null };
    }

    /// <summary>
    /// Load a user record INCLUDING the password hash. Reserved for
    /// credential-flow callers (login, password rotation) where the hash
    /// is required. Most read paths should use <see cref="LoadUserMetaAsync"/>
    /// instead so a forgotten check can't accidentally leak hashes.
    /// </summary>
    public async Task<User?> LoadUserMetaForAuthAsync(string shortname, string? actor = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {UserColumns} FROM users WHERE shortname=$1",
            conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var user = HydrateUser(reader);

        if (_engine is not null)
        {
            // A user reading their OWN row is treated as their own resource —
            // the "own" condition resolves naturally via the OwnerShortname
            // match. Reading other users requires a separate grant.
            await _engine.RequireAsync(actor,
                "view",
                new Locator(ResourceType.User, user.SpaceName, user.Subpath, user.Shortname),
                ResourceContext.FromUser(user), null, ct).ConfigureAwait(false);
        }
        return user;
    }

    // Returns the merged role->permission set for a user as it lives in the
    // user_permissions_cache table. Python's get_user_permissions reads the
    // same cache; if it's empty for a user the caller should regenerate it
    // via the dmart server, since the cache is built from a graph traversal
    // (roles → permissions → acl) that's deliberately not duplicated here.
    //
    // RBAC: an actor can read THEIR own permission set freely; reading
    // someone else's requires a `view` grant on that user row.
    //
    // The result is cached in-process for UserPermissionsCacheTtl (5 min)
    // keyed by user shortname. The cache is invalidated whenever this
    // adapter writes a User/Role/Permission row; out-of-band edits should
    // call InvalidateUserPermissionsCache.
    public async Task<Dictionary<string, object?>?> GetUserPermissionsAsync(string shortname,
        string? actor = null, CancellationToken ct = default)
    {
        if (_engine is not null && actor is not null && actor != shortname)
        {
            // Force a load → permission check on the target user before
            // returning their cached permission set.
            _ = await LoadUserMetaAsync(shortname, actor, ct).ConfigureAwait(false);
        }

        if (_userPermissionsCache.TryGetValue(shortname, out var cached) && cached.Expiry > DateTime.UtcNow)
            return cached.Perms;

        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT permissions FROM user_permissions_cache WHERE user_shortname=$1",
            conn);
        cmd.Parameters.Add(new() { Value = shortname });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        Dictionary<string, object?>? perms = null;
        if (await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0))
        {
            var raw = reader.GetString(0);
            perms = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw, _json);
        }
        _userPermissionsCache[shortname] = (DateTime.UtcNow + UserPermissionsCacheTtl, perms);
        return perms;
    }

    /// <summary>
    /// Idempotent bootstrap of the `management` space row. The full schema
    /// (tables, indexes, enums) is owned by the dmart server's
    /// SchemaInitializer — this SDK does not recreate it.
    /// </summary>
    /// <param name="ownerShortname">
    /// Shortname stamped into the inserted row. Defaults to "dmart" to
    /// match dmart Python's bootstrap default; pass a different value when
    /// calling from a non-default-admin context.
    /// </param>
    public async Task InitializeSpacesAsync(string ownerShortname = "dmart",
        string? actor = null, CancellationToken ct = default)
    {
        if (_engine is not null)
        {
            // Bootstrapping is privileged — require `create` on the
            // management space. Effectively limits this to super_admin.
            await _engine.RequireAsync(actor,
                "create",
                new Locator(ResourceType.Space, "management", "/", "management"),
                null, null, ct).ConfigureAwait(false);
        }
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO spaces (uuid, shortname, is_active, created_at, updated_at, owner_shortname, resource_type)
            VALUES (gen_random_uuid(), 'management', TRUE, NOW(), NOW(), $1, 'space')
            ON CONFLICT (shortname) DO NOTHING
            """, conn);
        cmd.Parameters.Add(new() { Value = ownerShortname });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------------

    private async Task UpsertEntryInternalAsync(Entry e, CancellationToken ct)
    {
        var subpath = Locator.NormalizeSubpath(e.Subpath);
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO entries (uuid, shortname, space_name, subpath, is_active, slug,
                                 displayname, description, tags, created_at, updated_at,
                                 owner_shortname, owner_group_shortname, acl, payload, relationships,
                                 last_checksum_history, resource_type,
                                 state, is_open, workflow_shortname, collaborators,
                                 resolution_reason, query_policies)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24)
            ON CONFLICT (shortname, space_name, subpath) DO UPDATE SET
                is_active = EXCLUDED.is_active,
                slug = EXCLUDED.slug,
                displayname = EXCLUDED.displayname,
                description = EXCLUDED.description,
                tags = EXCLUDED.tags,
                updated_at = EXCLUDED.updated_at,
                owner_shortname = EXCLUDED.owner_shortname,
                owner_group_shortname = EXCLUDED.owner_group_shortname,
                acl = EXCLUDED.acl,
                payload = EXCLUDED.payload,
                relationships = EXCLUDED.relationships,
                last_checksum_history = EXCLUDED.last_checksum_history,
                resource_type = EXCLUDED.resource_type,
                state = EXCLUDED.state,
                is_open = EXCLUDED.is_open,
                workflow_shortname = EXCLUDED.workflow_shortname,
                collaborators = EXCLUDED.collaborators,
                resolution_reason = EXCLUDED.resolution_reason,
                query_policies = EXCLUDED.query_policies
            """, conn);

        cmd.Parameters.Add(new() { Value = Guid.Parse(e.Uuid) });
        cmd.Parameters.Add(new() { Value = e.Shortname });
        cmd.Parameters.Add(new() { Value = e.SpaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = e.IsActive });
        cmd.Parameters.Add(new() { Value = (object?)e.Slug ?? DBNull.Value });
        cmd.Parameters.Add(JsonbHelpers.ToJsonbParameter("@displayname", e.Displayname, _json));
        cmd.Parameters.Add(JsonbHelpers.ToJsonbParameter("@description", e.Description, _json));
        cmd.Parameters.Add(JsonbHelpers.ToJsonbParameter("@tags", e.Tags, _json));
        cmd.Parameters.Add(new() { Value = e.CreatedAt == default ? DateTime.UtcNow : e.CreatedAt });
        cmd.Parameters.Add(new() { Value = e.UpdatedAt == default ? DateTime.UtcNow : e.UpdatedAt });
        cmd.Parameters.Add(new() { Value = e.OwnerShortname });
        cmd.Parameters.Add(new() { Value = (object?)e.OwnerGroupShortname ?? DBNull.Value });
        cmd.Parameters.Add(JsonbHelpers.ToJsonbParameter("@acl", e.Acl, _json));
        cmd.Parameters.Add(JsonbHelpers.ToJsonbParameter("@payload", e.Payload, _json));
        cmd.Parameters.Add(JsonbHelpers.ToJsonbParameter("@relationships", e.Relationships, _json));
        cmd.Parameters.Add(new() { Value = (object?)e.LastChecksumHistory ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = EnumWire(e.ResourceType) });
        cmd.Parameters.Add(new() { Value = (object?)e.State ?? DBNull.Value });
#pragma warning disable CA1508 // bool? boxed via (object?) cast IS null when source is null; the ?? is load-bearing.
        cmd.Parameters.Add(new() { Value = (object?)e.IsOpen ?? DBNull.Value });
#pragma warning restore CA1508
        cmd.Parameters.Add(new() { Value = (object?)e.WorkflowShortname ?? DBNull.Value });
        cmd.Parameters.Add(JsonbHelpers.ToJsonbParameter("@collaborators", e.Collaborators, _json));
        cmd.Parameters.Add(new() { Value = (object?)e.ResolutionReason ?? DBNull.Value });
        var qp = new NpgsqlParameter("@query_policies", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = (object?)e.QueryPolicies?.ToArray() ?? Array.Empty<string>(),
        };
        cmd.Parameters.Add(qp);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private Entry HydrateEntry(NpgsqlDataReader r)
    {
        return new Entry
        {
            Uuid = r.GetGuid(0).ToString(),
            Shortname = r.GetString(1),
            SpaceName = r.GetString(2),
            Subpath = r.GetString(3),
            IsActive = r.GetBoolean(4),
            Slug = r.IsDBNull(5) ? null : r.GetString(5),
            Displayname = r.ReadJsonb<Translation>(6, _json),
            Description = r.ReadJsonb<Translation>(7, _json),
            Tags = r.ReadJsonb<List<string>>(8, _json) ?? new(),
            CreatedAt = r.GetDateTime(9),
            UpdatedAt = r.GetDateTime(10),
            OwnerShortname = r.GetString(11),
            OwnerGroupShortname = r.IsDBNull(12) ? null : r.GetString(12),
            Acl = r.ReadJsonb<List<AclEntry>>(13, _json),
            Payload = r.ReadJsonb<Payload>(14, _json),
            Relationships = r.ReadJsonb<List<Dictionary<string, object>>>(15, _json),
            LastChecksumHistory = r.IsDBNull(16) ? null : r.GetString(16),
            ResourceType = ParseEnum<ResourceType>(r.GetString(17)),
            State = r.IsDBNull(18) ? null : r.GetString(18),
            IsOpen = r.IsDBNull(19) ? null : r.GetBoolean(19),
            WorkflowShortname = r.IsDBNull(20) ? null : r.GetString(20),
            Collaborators = r.ReadJsonb<Dictionary<string, string>>(21, _json),
            ResolutionReason = r.IsDBNull(22) ? null : r.GetString(22),
            QueryPolicies = r.IsDBNull(23) ? null : ((string[])r.GetValue(23)).ToList(),
        };
    }

    private Space HydrateSpace(NpgsqlDataReader r)
    {
        // Spaces are addressed by their shortname; the canonical Space record
        // also requires a SpaceName + Subpath ("/" by convention) for the
        // Metas base, so populate them from the same column.
        var shortname = r.GetString(1);
        var rawLangs = r.ReadJsonb<List<string>>(12, _json);
        return new Space
        {
            Uuid = r.GetGuid(0).ToString(),
            Shortname = shortname,
            SpaceName = shortname,
            Subpath = "/",
            IsActive = r.GetBoolean(2),
            Displayname = r.ReadJsonb<Translation>(3, _json),
            Description = r.ReadJsonb<Translation>(4, _json),
            CreatedAt = r.GetDateTime(5),
            UpdatedAt = r.GetDateTime(6),
            OwnerShortname = r.GetString(7),
            OwnerGroupShortname = r.IsDBNull(8) ? null : r.GetString(8),
            Acl = r.ReadJsonb<List<AclEntry>>(9, _json),
            RootRegistrationSignature = r.IsDBNull(10) ? "" : r.GetString(10),
            ActivePlugins = r.ReadJsonb<List<string>>(11, _json),
            Languages = (rawLangs ?? new()).Select(JsonbHelpers.ParseEnumMember<Language>).ToList(),
            CaptureMisses = !r.IsDBNull(13) && r.GetBoolean(13),
            CheckHealth = !r.IsDBNull(14) && r.GetBoolean(14),
        };
    }

    private User HydrateUser(NpgsqlDataReader r)
    {
        return new User
        {
            Uuid = r.GetGuid(0).ToString(),
            Shortname = r.GetString(1),
            SpaceName = r.GetString(2),
            Subpath = r.GetString(3),
            IsActive = r.GetBoolean(4),
            Displayname = r.ReadJsonb<Translation>(5, _json),
            Description = r.ReadJsonb<Translation>(6, _json),
            CreatedAt = r.GetDateTime(7),
            UpdatedAt = r.GetDateTime(8),
            OwnerShortname = r.GetString(9),
            OwnerGroupShortname = r.IsDBNull(10) ? null : r.GetString(10),
            Acl = r.ReadJsonb<List<AclEntry>>(11, _json),
            Password = r.IsDBNull(12) ? null : r.GetString(12),
            Roles = r.ReadJsonb<List<string>>(13, _json) ?? new(),
            Groups = r.ReadJsonb<List<string>>(14, _json) ?? new(),
            Type = ParseEnum<UserType>(r.GetString(15)),
            Language = ParseEnum<Language>(r.GetString(16)),
            Email = r.IsDBNull(17) ? null : r.GetString(17),
            Msisdn = r.IsDBNull(18) ? null : r.GetString(18),
            IsEmailVerified = r.GetBoolean(19),
            IsMsisdnVerified = r.GetBoolean(20),
            ForcePasswordChange = r.GetBoolean(21),
            GoogleId = r.IsDBNull(22) ? null : r.GetString(22),
            FacebookId = r.IsDBNull(23) ? null : r.GetString(23),
            AttemptCount = r.IsDBNull(24) ? null : r.GetInt32(24),
            QueryPolicies = r.IsDBNull(25) ? new() : ((string[])r.GetValue(25)).ToList(),
        };
    }

    // dmart stores enum values as snake_case strings (matching Python's
    // Enum wire format). Dmart.Models enums carry [EnumMember(Value=...)]
    // for compounds like PluginWrapper → "plugin_wrapper" and
    // DataAsset → "data_asset"; read those via reflection so the wire
    // strings match what the server writes.
    private static string EnumWire<TEnum>(TEnum value) where TEnum : struct, Enum
        => JsonbHelpers.EnumMember(value);

    private static T ParseEnum<T>(string value) where T : struct, Enum
        => JsonbHelpers.ParseEnumMember<T>(value);

    private static NpgsqlParameter Clone(NpgsqlParameter p)
    {
        var c = new NpgsqlParameter(p.ParameterName, p.Value);
        // Preserve NpgsqlDbType when the source set it explicitly — otherwise
        // jsonb-typed parameters (e.g. payload @> $jsonb containment literals
        // built by SearchExpressionParser) get re-inferred as text on the
        // second-command clone and Postgres rejects `jsonb @> text`.
        if (p.NpgsqlDbType != NpgsqlTypes.NpgsqlDbType.Unknown)
            c.NpgsqlDbType = p.NpgsqlDbType;
        return c;
    }

    private static string Quote(string column)
    {
        // Whitelist: only allow [a-zA-Z0-9_] in column names passed through.
        // We deliberately don't double-quote because dmart's columns are all
        // lowercase snake_case and Postgres treats them as such unquoted.
        foreach (var c in column)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                throw new ArgumentException($"Invalid column name: {column}", nameof(column));
        }
        return column;
    }
}
