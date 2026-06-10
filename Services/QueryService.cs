using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.QueryGrammar;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.Services;

// /managed/query service. Mirrors Python's adapter.query() dispatch logic:
//
//   QueryType.Spaces        → spaces table (management/ only)
//   QueryType.History        → histories table
//   QueryType.Attachments    → attachments table
//   QueryType.Tags           → entries table, SQL aggregation
//   management/users         → users table
//   management/roles         → roles table
//   management/permissions   → permissions table
//   everything else          → entries table
//
// Response envelope: { status, records, attributes: { total, returned } }
public sealed class QueryService(
    EntryRepository entries,
    SpaceRepository spaces,
    UserRepository users,
    AccessRepository access,
    AttachmentRepository attachments,
    HistoryRepository history,
    PermissionService perms,
    SpaceEventLogger eventLogger,
    Db db,
    IOptions<DmartSettings> settings,
    ILogger<QueryService> logger)
{
    // Permission gate for query methods. Tries "view" first (works for anonymous +
    // authenticated), then "query" — Python exempts the "query" action from
    // condition checks (access_control.py:218), so a permission with
    // `conditions:["is_active"]` still grants listing rights even when no
    // specific resource has been loaded yet (the SQL adapter applies the
    // is_active filter separately via query_policies). The anonymous caller
    // is specifically the common case that hits this path — a public
    // `world` permission typically carries is_active as a condition.
    // For root queries where the user's permissions are keyed to specific
    // subpaths (not "/"), falls back to HasAnyAccessToSpaceAsync.
    private async Task<bool> CanQueryAsync(string? actor, ResourceType rt, string spaceName, string subpath, CancellationToken ct)
    {
        var probe = new Locator(rt, spaceName, subpath, "*");
        if (await perms.CanAsync(actor, "view", probe, ct: ct)) return true;
        if (await perms.CanAsync(actor, "query", probe, ct: ct)) return true;
        return subpath == "/" && await perms.HasAnyAccessToSpaceAsync(actor, spaceName, ct);
    }

    // Python returns (0, []) when the user has no matching query policies — a success
    // with zero records, not an error. This matches that behavior.
    private static Response EmptyQueryResponse() =>
        Response.Ok(Array.Empty<Record>(), new() { ["total"] = 0, ["returned"] = 0 });

    private async Task<List<string>?> GetActorQueryPoliciesAsync(Query q, string? actor, CancellationToken ct)
    {
        if (actor is null) return null;
        return await perms.BuildUserQueryPoliciesAsync(actor, q.SpaceName, q.Subpath ?? "/", ct);
    }

    public async Task<Response> ExecuteAsync(Query q, string? actor, CancellationToken ct = default)
    {
        // Clamp limit: default to 100, cap at MaxQueryLimit.
        var maxLimit = settings.Value.MaxQueryLimit;
        var limit = q.Limit == -1 ? (maxLimit > 0 ? maxLimit : 100) : (q.Limit <= 0 ? 100 : q.Limit);
        if (maxLimit > 0 && limit > maxLimit) limit = maxLimit;
        q = q with { Limit = limit };

        if (string.IsNullOrEmpty(q.SpaceName))
            return Response.Fail(InternalErrorCode.INVALID_DATA, "space_name is required", ErrorTypes.Request);

        // A cardinality-changing join (inner/right/outer) filters or appends
        // records AFTER the base query runs. If we let the base query apply
        // SQL LIMIT/OFFSET first, the join would then drop/append rows INSIDE
        // an already-paged window — producing short pages, a `total` that
        // reflects the pre-join base count, and rows silently skipped across
        // pages. To get "inner-join then paginate" semantics we fetch the base
        // set unpaginated (up to MaxQueryLimit), run the join over the whole
        // set, then page the JOINED result below. This is a deliberate
        // divergence from Python's adapter.py, which paginates BEFORE
        // _apply_client_joins (same latent bug). A `left` join keeps base
        // cardinality, so it stays on the cheap SQL-paged path untouched.
        var userLimit = limit;
        var userOffset = q.Offset;
        var repaginateAfterJoin = q.Join is { Count: > 0 } jset
            && jset.Any(j => (j.Type ?? JoinType.Left) is JoinType.Inner or JoinType.Right or JoinType.Outer);
        var baseCap = maxLimit > 0 ? maxLimit : 10000;

        // For the re-paginated path, fetch the full base set (offset 0, capped)
        // and skip the base COUNT — `total` is recomputed post-join below.
        var dispatchQuery = repaginateAfterJoin
            ? q with { Limit = baseCap, Offset = 0, RetrieveTotal = false }
            : q;

        var response = dispatchQuery.Type switch
        {
            QueryType.Spaces => await QuerySpacesAsync(dispatchQuery, actor, ct),
            QueryType.History => await QueryHistoryAsync(dispatchQuery, actor, ct),
            QueryType.Attachments => await QueryAttachmentsAsync(dispatchQuery, actor, ct),
            QueryType.Tags => await QueryTagsAsync(dispatchQuery, actor, ct),
            QueryType.Aggregation => await QueryAggregationAsync(dispatchQuery, actor, "entries", ct),
            QueryType.AttachmentsAggregation => await QueryAggregationAsync(dispatchQuery, actor, "attachments", ct),
            QueryType.Counters => await QueryCountersAsync(dispatchQuery, actor, ct),
            QueryType.Events => await QueryEventsAsync(dispatchQuery, actor, ct),
            _ => await DispatchTableQuery(dispatchQuery, actor, ct),
        };

        // Python parity: client-side joins run against the materialized result
        // list. Mirror of dmart_plain/backend/data_adapters/sql/adapter.py:
        // _apply_client_joins. Each JoinQuery fires a second ExecuteAsync with
        // a synthesized search term "@<right_path>:<left_vals>" and matches
        // right results to each base record; matches land under
        // record.attributes["join"][<alias>].
        if (q.Join is { Count: > 0 } joins
            && response.Status == Status.Success
            && response.Records is { Count: > 0 } records)
        {
            // No silent caps: when the base scan hits the cap the join+paginate
            // window is incomplete for pages beyond it — surface it in the log.
            if (repaginateAfterJoin && records.Count >= baseCap)
                logger.LogWarning(
                    "client_join base set hit the {Cap}-row cap; join pagination beyond it is incomplete",
                    baseCap);
            try
            {
                var (joined, jqFail) = await ApplyClientJoinsAsync(records, joins, actor, ct);
                if (jqFail is not null)
                    // Python propagates jq failures up as HTTP 400. Match that —
                    // the client asked for a filter and we couldn't honor it, so
                    // returning un-filtered data would be misleading.
                    return jqFail;

                if (repaginateAfterJoin)
                {
                    // Inner/right/outer: page the JOINED set and recompute
                    // `total` as the post-join cardinality. The base COUNT was
                    // suppressed above — it would have been the pre-join
                    // base-table count, meaningless once the join drops/appends.
                    var total = joined.Count;
                    var page = joined.Skip(Math.Max(0, userOffset)).Take(Math.Max(1, userLimit)).ToList();
                    var pagedAttrs = response.Attributes is not null
                        ? new Dictionary<string, object>(response.Attributes, StringComparer.Ordinal)
                        : new Dictionary<string, object>(StringComparer.Ordinal);
                    pagedAttrs["total"] = total;
                    pagedAttrs["returned"] = page.Count;
                    response = response with { Records = page, Attributes = pagedAttrs };
                }
                else
                {
                    // Left join keeps base cardinality, so the SQL page is still
                    // the correct window; just resync `returned` (the join only
                    // attaches data, never changes the count) and keep `total`
                    // as the base-table count.
                    Dictionary<string, object>? newAttrs = null;
                    if (response.Attributes is not null && response.Attributes.ContainsKey("returned"))
                    {
                        newAttrs = new Dictionary<string, object>(response.Attributes, StringComparer.Ordinal)
                        {
                            ["returned"] = joined.Count,
                        };
                    }
                    response = response with
                    {
                        Records = joined,
                        Attributes = newAttrs ?? response.Attributes,
                    };
                }
            }
            catch (Exception ex)
            {
                // Python swallows non-jq join errors with a print; match that:
                // return the un-joined base results rather than failing the
                // whole query. jq failures are already handled above via the
                // tuple return — this catch is for join-algorithm bugs only.
                logger.LogWarning(ex, "client_join join failed");
                if (repaginateAfterJoin)
                {
                    // The base set was fetched unpaginated for the join; on a
                    // join failure, fall back to the user's page window over the
                    // un-joined base records rather than dumping the whole capped
                    // scan (up to MaxQueryLimit rows) at the caller.
                    var page = records.Skip(Math.Max(0, userOffset)).Take(Math.Max(1, userLimit)).ToList();
                    var attrs = response.Attributes is not null
                        ? new Dictionary<string, object>(response.Attributes, StringComparer.Ordinal)
                        : new Dictionary<string, object>(StringComparer.Ordinal);
                    attrs["returned"] = page.Count;
                    response = response with { Records = page, Attributes = attrs };
                }
            }
        }

        return response;
    }

    // ====================================================================
    // TABLE DISPATCH (mirrors Python's set_table_for_query)
    // ====================================================================

    private async Task<Response> DispatchTableQuery(Query q, string? actor, CancellationToken ct)
    {
        var mgmt = settings.Value.ManagementSpace;

        // Python: when space == management, route /users → Users table, etc.
        if (string.Equals(q.SpaceName, mgmt, StringComparison.Ordinal))
        {
            var sub = q.Subpath.TrimStart('/');
            if (sub == "users" || sub.StartsWith("users/", StringComparison.Ordinal))
                return await QueryUsersAsync(q, actor, ct);
            if (sub == "roles" || sub.StartsWith("roles/", StringComparison.Ordinal))
                return await QueryRolesAsync(q, actor, ct);
            if (sub == "groups" || sub.StartsWith("groups/", StringComparison.Ordinal))
                return await QueryGroupsAsync(q, actor, ct);
            if (sub == "permissions" || sub.StartsWith("permissions/", StringComparison.Ordinal))
                return await QueryPermissionsAsync(q, actor, ct);
        }

        return await QueryEntriesAsync(q, actor, ct);
    }

    // ====================================================================
    // SPACES
    // ====================================================================

    private async Task<Response> QuerySpacesAsync(Query q, string? actor, CancellationToken ct)
    {
        var managementSpace = settings.Value.ManagementSpace;
        var normalizedSubpath = string.IsNullOrEmpty(q.Subpath) ? "/" : q.Subpath;
        if (!string.Equals(q.SpaceName, managementSpace, StringComparison.Ordinal) || normalizedSubpath != "/")
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                $"spaces query requires space_name=\"{managementSpace}\" and subpath=\"/\"",
                ErrorTypes.Request);

        var all = await spaces.ListAsync(ct);
        // Batched: walk the user's permissions ONCE and intersect with the space
        // list, rather than N separate permission lookups (the old N+1 pattern).
        var allowed = await perms.GetAccessibleSpacesAsync(
            actor, all.Select(s => s.Shortname), ct);
        var visible = all.Where(s => allowed.Contains(s.Shortname)).ToList();

        var total = visible.Count;
        var page = visible.Skip(Math.Max(0, q.Offset)).Take(Math.Max(1, q.Limit)).ToList();
        var records = page.Select(SpaceMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = total, ["returned"] = records.Count });
    }

    // ====================================================================
    // USERS (management/users)
    // ====================================================================

    private async Task<Response> QueryUsersAsync(Query q, string? actor, CancellationToken ct)
    {
        if (!await CanQueryAsync(actor, ResourceType.User, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        var policies = await GetActorQueryPoliciesAsync(q, actor, ct);
        if (actor is not null && policies!.Count == 0) return EmptyQueryResponse();

        var pageTask = actor is not null
            ? users.QueryAsync(q, actor, policies, ct)
            : users.QueryAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : (actor is not null
                ? users.CountQueryAsync(q, actor, policies, ct)
                : users.CountQueryAsync(q, ct));
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask).Select(UserMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // ROLES (management/roles)
    // ====================================================================

    private async Task<Response> QueryRolesAsync(Query q, string? actor, CancellationToken ct)
    {
        if (!await CanQueryAsync(actor, ResourceType.Role, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        var policies = await GetActorQueryPoliciesAsync(q, actor, ct);
        if (actor is not null && policies!.Count == 0) return EmptyQueryResponse();

        var pageTask = actor is not null
            ? access.QueryRolesAsync(q, actor, policies, ct)
            : access.QueryRolesAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : (actor is not null
                ? access.CountRolesQueryAsync(q, actor, policies, ct)
                : access.CountRolesQueryAsync(q, ct));
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask).Select(RoleMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // GROUPS (management/groups)
    // ====================================================================

    private async Task<Response> QueryGroupsAsync(Query q, string? actor, CancellationToken ct)
    {
        if (!await CanQueryAsync(actor, ResourceType.Group, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        var policies = await GetActorQueryPoliciesAsync(q, actor, ct);
        if (actor is not null && policies!.Count == 0) return EmptyQueryResponse();

        var pageTask = actor is not null
            ? access.QueryGroupsAsync(q, actor, policies, ct)
            : access.QueryGroupsAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : (actor is not null
                ? access.CountGroupsQueryAsync(q, actor, policies, ct)
                : access.CountGroupsQueryAsync(q, ct));
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask).Select(GroupMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // PERMISSIONS (management/permissions)
    // ====================================================================

    private async Task<Response> QueryPermissionsAsync(Query q, string? actor, CancellationToken ct)
    {
        if (!await CanQueryAsync(actor, ResourceType.Permission, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        var policies = await GetActorQueryPoliciesAsync(q, actor, ct);
        if (actor is not null && policies!.Count == 0) return EmptyQueryResponse();

        var pageTask = actor is not null
            ? access.QueryPermissionsAsync(q, actor, policies, ct)
            : access.QueryPermissionsAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : (actor is not null
                ? access.CountPermissionsQueryAsync(q, actor, policies, ct)
                : access.CountPermissionsQueryAsync(q, ct));
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask).Select(PermissionMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // ATTACHMENTS
    // ====================================================================

    private async Task<Response> QueryAttachmentsAsync(Query q, string? actor, CancellationToken ct)
    {
        // Python: get_user_query_policies() → if empty return (0, []). Row-level ACL
        // is skipped for attachments (see AppendAclFilter), but the policy gate remains.
        if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        var pageTask = attachments.QueryAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : attachments.CountQueryAsync(q, ct);
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask).Select(AttachmentMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // HISTORY
    // ====================================================================

    private async Task<Response> QueryHistoryAsync(Query q, string? actor, CancellationToken ct)
    {
        // Python blocks anonymous users for history queries.
        if (actor is null)
            return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED,
                "history queries require authentication", ErrorTypes.Auth);

        if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        var pageTask = history.QueryHistoryAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : history.CountHistoryQueryAsync(q, ct);
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask).Select(HistoryMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // EVENTS (file-based, parity with Python's spaces_folder/<space>/.dm/events.jsonl.log)
    // ====================================================================

    // Reads the per-space audit log written by SpaceEventLogger. Each line is
    // one JSON object; we apply the same Query filters Python does — subpath
    // (prefix unless ExactSubpath), shortname allow-list, FromDate/ToDate
    // against the timestamp, then SortType (newest-first by default), then
    // offset/limit. Returns an empty success when the file doesn't exist
    // (a fresh space, or SpacesFolder unconfigured) — same shape as Python's
    // empty-list-on-missing behavior.
    //
    // No PG fallback: this matches Python exactly. If you want events for
    // pre-existing entries, set SpacesFolder and re-trigger the actions, or
    // backfill the log file manually.
    private async Task<Response> QueryEventsAsync(Query q, string? actor, CancellationToken ct)
    {
        // Python blocks anonymous users for the events feed.
        if (actor is null)
            return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED,
                "events queries require authentication", ErrorTypes.Auth);

        // ACL is space-level only — matches Python upstream. The events feed
        // surfaces actions across all subpaths in the space, so a user with
        // read on / can see action timestamps for sub-paths they couldn't
        // read directly. Two reasons we keep it: (a) parity with Python's
        // events_query, (b) per-event ACL would require resolving each line
        // back to its locator, defeating the file-tail performance model.
        // TODO(future): if/when we add per-subpath ACL on the events feed,
        // do it as a post-filter against `resource.subpath` in TryParseEventLine
        // — the data is already there.
        if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        if (!eventLogger.Enabled)
            return Response.Ok(Array.Empty<Record>(), new() { ["total"] = 0, ["returned"] = 0 });

        var path = eventLogger.ResolveLogPath(q.SpaceName);
        if (!File.Exists(path))
            return Response.Ok(Array.Empty<Record>(), new() { ["total"] = 0, ["returned"] = 0 });

        var matches = new List<(DateTime Ts, Record Rec)>();
        // skippedCorrupt counts lines we silently dropped so we can surface
        // log-file corruption to ops once per query rather than per line —
        // a tail of a half-written final line should not poison the whole
        // response, but a rotated/truncated file dropping every line should
        // be visible.
        var skippedCorrupt = 0;
        // Open with FileShare.ReadWrite so a concurrent SpaceEventLogger.LogAsync
        // append doesn't lock us out while a query is running.
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!TryParseEventLine(line, q, out var ts, out var rec))
            {
                skippedCorrupt++;
                continue;
            }
            matches.Add((ts, rec!));
        }
        if (skippedCorrupt > 0)
            logger.LogWarning("events: {Count} unparseable lines skipped in {Path}",
                skippedCorrupt, path);

        // Default sort: newest-first (Python's default for the events feed).
        // Ascending only when SortType.Ascending is requested explicitly.
        if (q.SortType == Dmart.Models.Enums.SortType.Ascending)
            matches.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        else
            matches.Sort((a, b) => b.Ts.CompareTo(a.Ts));

        var total = matches.Count;
        var page = matches.Skip(q.Offset).Take(q.Limit).Select(t => t.Rec).ToList();
        return Response.Ok(page, new()
        {
            ["total"] = q.RetrieveTotal == false ? -1 : total,
            ["returned"] = page.Count,
        });
    }

    // Parse one line of events.jsonl and apply the only filters Python's
    // events_query (backend/data_adapters/sql/adapter_helpers.py) honors —
    // FromDate, ToDate, and the substring `search`. Returns false when the
    // line isn't well-formed or the date filter rejects it.
    //
    // Per Python parity:
    //   - `filter_shortnames`, `filter_types`, `subpath`, `exact_subpath` are
    //     IGNORED (events live at the space root; no per-subpath bucketing).
    //   - The whole parsed event JSON becomes the Record's `attributes`.
    //   - `resource_type`, `shortname`, `subpath` of the Record come from
    //     `resource.type` / `resource.shortname` / `resource.subpath`.
    //
    // Also tolerates the legacy flat shape we wrote in an earlier revision so
    // an operator's pre-existing logs keep parsing across the upgrade.
    //
    // Internal so the unit suite can drive it without spinning up the full
    // QueryService (which needs PG, perms, and the DI graph).
    internal static bool TryParseEventLine(string line, Query q, out DateTime ts, out Record? rec)
    {
        ts = default;
        rec = null;

        // Substring search runs against the raw line first — Python's
        // process_jsonl_file does the same, before JSON parsing. Cheap and
        // matches across the whole event payload.
        if (!string.IsNullOrEmpty(q.Search) && line.IndexOf(q.Search, StringComparison.Ordinal) < 0)
            return false;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return false; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            // The Locator block is at root.resource on Python lines; on legacy
            // flat lines the same fields live at root. Resolve once and read
            // through the same JsonElement pointer below.
            var hasResource = root.TryGetProperty("resource", out var resEl)
                && resEl.ValueKind == JsonValueKind.Object;
            var src = hasResource ? resEl : root;

            // Note on slash form: Record normalizes Subpath via Trim('/'),
            // so "users" is stored on the outer Record while
            // attributes.resource.subpath stays as the raw on-disk "/users".
            // Both are correct by project convention — outer Record.Subpath
            // is the canonical wire format, the inner block is the verbatim
            // log payload. Clients reading either should know which they're
            // reading.
            var subpath = src.TryGetProperty("subpath", out var spEl) && spEl.ValueKind == JsonValueKind.String
                ? spEl.GetString() ?? "/" : "/";
            var shortname = src.TryGetProperty("shortname", out var snEl) && snEl.ValueKind == JsonValueKind.String
                ? snEl.GetString() ?? "" : "";

            // Timestamp parsing — written by SpaceEventLogger as
            // "yyyy-MM-ddTHH:mm:ss.ffffff" (Python parity, 6-digit micros).
            // DateTime.TryParse already handles the legacy 3-digit format too.
            if (!root.TryGetProperty("timestamp", out var tsEl)
                || tsEl.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(tsEl.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out ts))
                ts = DateTime.MinValue;

            if (q.FromDate is { } from && ts < from) return false;
            if (q.ToDate   is { } to   && ts > to) return false;

            // Resource type — Python writes `resource.type`; legacy flat
            // shape wrote `resource_type` at the root. Try both.
            ResourceType? rt = null;
            string? rtStr = null;
            if (hasResource && src.TryGetProperty("type", out var rtEl) && rtEl.ValueKind == JsonValueKind.String)
                rtStr = rtEl.GetString();
            else if (root.TryGetProperty("resource_type", out var legacyEl) && legacyEl.ValueKind == JsonValueKind.String)
                rtStr = legacyEl.GetString();

            if (!string.IsNullOrEmpty(rtStr))
            {
                try { rt = JsonSerializer.Deserialize($"\"{rtStr}\"", DmartJsonContext.Default.ResourceType); }
                catch (JsonException) { rt = null; }
            }

            // Python parity: Record.attributes is the WHOLE parsed event
            // JSON, not a curated subset. Walk every top-level key into a
            // dict so the wire response carries `resource`, `request`,
            // `timestamp`, `user_shortname`, and the inner `attributes`
            // exactly as written.
            var attrs = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
                attrs[prop.Name] = prop.Value.Clone();

            rec = new Record
            {
                ResourceType = rt ?? ResourceType.Content,
                Shortname = string.IsNullOrEmpty(shortname) ? "_" : shortname,
                Subpath = subpath,
                Attributes = attrs,
            };
            return true;
        }
    }

    // ====================================================================
    // TAGS (SQL aggregation)
    // ====================================================================

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: SQL is a constant-string template; AppendAclFilter binds the trusted internal `effectiveActor` via $N parameters and the LIMIT/OFFSET integers are bound the same way.")]
    private async Task<Response> QueryTagsAsync(Query q, string? actor, CancellationToken ct)
    {
        // Python parity (adapter.py:1510-1520): no resource-type-specific
        // preflight. Resolve the policy list (anonymous → "anonymous") and
        // bail when it's empty. The ACL filter then runs on every path.
        var policies = await perms.BuildUserQueryPoliciesAsync(actor, q.SpaceName, q.Subpath ?? "/", ct);
        if (policies.Count == 0) return EmptyQueryResponse();

        q = await MergeFilterFieldsValuesAsync(q, policies, actor, ct);

        var effectiveActor = actor ?? PermissionService.AnonymousUser;

        // SQL: unnest tags jsonb array, group by tag, count.
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args, "entries");
        var sql = new System.Text.StringBuilder($"""
            SELECT tag, COUNT(*) AS cnt
            FROM entries, jsonb_array_elements_text(tags) AS tag
            WHERE {where}
            """);
        QueryHelper.AppendAclFilter(sql, args, effectiveActor, "entries", policies);
        sql.Append("GROUP BY tag ORDER BY cnt DESC");
        // Apply limit/offset on the aggregated result.
        args.Add(new() { Value = Math.Max(1, q.Limit) });
        sql.Append($" LIMIT ${args.Count}");
        args.Add(new() { Value = Math.Max(0, q.Offset) });
        sql.Append($" OFFSET ${args.Count}");

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        foreach (var p in args) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var tags = new List<string>();
        var tagCounts = new Dictionary<string, object>();
        while (await reader.ReadAsync(ct))
        {
            var tag = reader.GetString(0);
            var count = reader.GetInt64(1);
            tags.Add(tag);
            tagCounts[tag] = (int)count;
        }

        // Python returns a single Record with the aggregated data in attributes.
        var record = new Record
        {
            ResourceType = ResourceType.Content,
            Shortname = "tags",
            Subpath = q.Subpath ?? "/",
            Attributes = new Dictionary<string, object>
            {
                ["tags"] = tags,
                ["tag_counts"] = tagCounts,
            },
        };
        return Response.Ok(new[] { record }, new() { ["total"] = tags.Count, ["returned"] = tags.Count });
    }

    // ====================================================================
    // ENTRIES (default path)
    // ====================================================================

    private async Task<Response> QueryEntriesAsync(Query q, string? actor, CancellationToken ct)
    {
        // Python parity (adapter.py:1500-1520, query_policies_helper.py:63-112):
        // skip the resource_type-specific preflight — it locked out every
        // caller whose permission resource_types didn't list "content" even
        // when the entries table holds the type they were authorized for
        // (Ticket, Folder, Schema, …). Compute the resource-type-agnostic
        // policy list and bail only when it's empty. Anonymous resolves to
        // user "anonymous" inside BuildUserQueryPoliciesAsync, matching
        // Python's `user_shortname = user_shortname if user_shortname else "anonymous"`.
        var policies = await perms.BuildUserQueryPoliciesAsync(actor, q.SpaceName, q.Subpath ?? "/", ct);
        if (policies.Count == 0) return EmptyQueryResponse();

        // Python parity (adapter.py:1529-1566): merge per-permission
        // filter_fields_values into q.Search before SQL is built. Each
        // matching permission contributes a `@field:value` clause that
        // narrows the result set to rows the user is authorized to see.
        q = await MergeFilterFieldsValuesAsync(q, policies, actor, ct);

        var effectiveActor = actor ?? PermissionService.AnonymousUser;
        var pageTask = entries.QueryAsync(q, effectiveActor, policies, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : entries.CountQueryAsync(q, effectiveActor, policies, ct);
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask)
            .Select(e => EntryMapper.ToRecord(e, q.SpaceName, q.RetrieveJsonPayload))
            .ToList();

        // If retrieve_attachments, fetch every parent's attachments in a SINGLE
        // round trip. Previously this was a fan-out (1 query per record, in
        // batches of 5 to bound concurrency) which, for a 100-record page,
        // meant 100 DB round trips. With DatabasePoolSize defaulting to 10+10
        // overflow, even a few concurrent retrieve_attachments=true callers
        // saturated the pool and serialized behind it.
        if (q.RetrieveAttachments && records.Count > 0)
        {
            var parents = new (string Subpath, string Shortname)[records.Count];
            for (var i = 0; i < records.Count; i++)
                parents[i] = (records[i].Subpath, records[i].Shortname);

            var grouped = await attachments.ListForParentsAsync(q.SpaceName, parents, ct);

            for (var i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                var normalized = Models.Core.Locator.NormalizeSubpath(rec.Subpath);
                var key = $"{normalized.TrimEnd('/')}/{rec.Shortname}";
                if (!grouped.TryGetValue(key, out var attList) || attList.Count == 0)
                    continue;

                records[i] = rec with
                {
                    Attachments = attList
                        .GroupBy(a => DataAdapters.Sql.JsonbHelpers.EnumMember(a.ResourceType))
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(a => AttachmentMapper.ToEntryRecord(a)).ToList())
                };
            }
        }

        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // AGGREGATION (GROUP BY + reducers)
    // ====================================================================

    private async Task<Response> QueryAggregationAsync(Query q, string? actor, string tableName, CancellationToken ct)
    {
        if (q.AggregationData is null)
            return Response.Fail(InternalErrorCode.MISSING_DATA,
                "aggregation_data required for aggregation queries", ErrorTypes.Request);

        // Python parity (adapter.py:1510-1520): policy list is the only gate.
        var policies = await perms.BuildUserQueryPoliciesAsync(actor, q.SpaceName, q.Subpath ?? "/", ct);
        if (policies.Count == 0) return EmptyQueryResponse();

        q = await MergeFilterFieldsValuesAsync(q, policies, actor, ct);

        var effectiveActor = actor ?? PermissionService.AnonymousUser;
        var rows = await QueryHelper.RunAggregationAsync(db, tableName, q, ct, effectiveActor, policies);

        // Convert each aggregation row to a Record with the grouped values + reducer results
        // in attributes. Python returns a single Record per group.
        var records = rows.Select(row =>
        {
            // Convert numeric types to int/double to avoid JsonTypeInfo issues with source-gen
            var attrs = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in row)
            {
                attrs[kv.Key] = kv.Value switch
                {
                    long l => (int)l,
                    decimal d => (double)d,
                    _ => kv.Value,
                };
            }
            return new Record
            {
                ResourceType = ResourceType.Content,
                Shortname = "aggregation",
                Subpath = q.Subpath ?? "/",
                Attributes = attrs,
            };
        }).ToList();

        return Response.Ok(records, new() { ["total"] = records.Count, ["returned"] = records.Count });
    }

    // ====================================================================
    // COUNTERS (raw count tuples)
    // ====================================================================

    private async Task<Response> QueryCountersAsync(Query q, string? actor, CancellationToken ct)
    {
        // Python runs the full query through table routing (users/roles/permissions/entries),
        // then returns records=[] with total and returned counts in attributes.
        // We route to the correct table's count method and compute returned from total/limit/offset.
        var resp = await DispatchCountersTable(q, actor, ct);
        return resp;
    }

    private async Task<Response> DispatchCountersTable(Query q, string? actor, CancellationToken ct)
    {
        var mgmt = settings.Value.ManagementSpace;

        // Route to the correct table and use the matching resource type for the
        // permission probe — management/users needs ResourceType.User, not Content.
        int total;
        if (string.Equals(q.SpaceName, mgmt, StringComparison.Ordinal))
        {
            var sub = (q.Subpath ?? "/").TrimStart('/');
            if (sub == "users" || sub.StartsWith("users/", StringComparison.Ordinal))
            {
                if (!await CanQueryAsync(actor, ResourceType.User, q.SpaceName, q.Subpath ?? "/", ct))
                    return EmptyQueryResponse();
                var policies = await GetActorQueryPoliciesAsync(q, actor, ct);
                if (actor is not null && policies!.Count == 0) return EmptyQueryResponse();
                total = actor is not null
                    ? await users.CountQueryAsync(q, actor, policies, ct)
                    : await users.CountQueryAsync(q, ct);
            }
            else if (sub == "roles" || sub.StartsWith("roles/", StringComparison.Ordinal))
            {
                if (!await CanQueryAsync(actor, ResourceType.Role, q.SpaceName, q.Subpath ?? "/", ct))
                    return EmptyQueryResponse();
                var policies = await GetActorQueryPoliciesAsync(q, actor, ct);
                if (actor is not null && policies!.Count == 0) return EmptyQueryResponse();
                total = actor is not null
                    ? await access.CountRolesQueryAsync(q, actor, policies, ct)
                    : await access.CountRolesQueryAsync(q, ct);
            }
            else if (sub == "permissions" || sub.StartsWith("permissions/", StringComparison.Ordinal))
            {
                if (!await CanQueryAsync(actor, ResourceType.Permission, q.SpaceName, q.Subpath ?? "/", ct))
                    return EmptyQueryResponse();
                var policies = await GetActorQueryPoliciesAsync(q, actor, ct);
                if (actor is not null && policies!.Count == 0) return EmptyQueryResponse();
                total = actor is not null
                    ? await access.CountPermissionsQueryAsync(q, actor, policies, ct)
                    : await access.CountPermissionsQueryAsync(q, ct);
            }
            else
            {
                // Python parity: policy list is the only gate (no rt-specific preflight).
                var policies = await perms.BuildUserQueryPoliciesAsync(actor, q.SpaceName, q.Subpath ?? "/", ct);
                if (policies.Count == 0) return EmptyQueryResponse();
                q = await MergeFilterFieldsValuesAsync(q, policies, actor, ct);
                total = await entries.CountQueryAsync(q, actor ?? PermissionService.AnonymousUser, policies, ct);
            }
        }
        else
        {
            var policies = await perms.BuildUserQueryPoliciesAsync(actor, q.SpaceName, q.Subpath ?? "/", ct);
            if (policies.Count == 0) return EmptyQueryResponse();
            q = await MergeFilterFieldsValuesAsync(q, policies, actor, ct);
            total = await entries.CountQueryAsync(q, actor ?? PermissionService.AnonymousUser, policies, ct);
        }

        var returned = Math.Min(Math.Max(total - Math.Max(0, q.Offset), 0), Math.Max(1, q.Limit));
        return Response.Ok(Array.Empty<Record>(), new() { ["total"] = total, ["returned"] = returned });
    }

    // ====================================================================
    // CLIENT-SIDE JOINS
    // Port of dmart_plain/backend/data_adapters/sql/adapter.py:
    // _apply_client_joins. For each JoinQuery, gathers left-path values from
    // the base records, issues a second ExecuteAsync with a synthesized
    // search term, then matches right results back into each base record's
    // attributes["join"][<alias>] list.
    // ====================================================================

    private async Task<(List<Record> Records, Response? Failure)> ApplyClientJoinsAsync(
        List<Record> baseRecords, List<JoinQuery> joins, string? actor, CancellationToken ct)
    {
        // Ensure every base record has a mutable Attributes dict with a "join"
        // sub-dict. Record.Attributes is init-only but Dictionary is mutable,
        // so we only need to rebuild the record when Attributes was null.
        for (var i = 0; i < baseRecords.Count; i++)
        {
            var br = baseRecords[i];
            if (br.Attributes is null)
            {
                baseRecords[i] = br with
                {
                    Attributes = new Dictionary<string, object> { ["join"] = new Dictionary<string, object>() }
                };
            }
            else if (!br.Attributes.ContainsKey("join"))
            {
                br.Attributes["join"] = new Dictionary<string, object>();
            }
        }

        foreach (var joinItem in joins)
        {
            if (string.IsNullOrEmpty(joinItem.JoinOn)
                || string.IsNullOrEmpty(joinItem.Alias)
                || joinItem.Query is null)
                continue;

            var parsedJoins = ParseJoinOn(joinItem.JoinOn);
            if (parsedJoins.Count == 0) continue;

            Query? subQuery;
            try
            {
                subQuery = JsonSerializer.Deserialize(joinItem.Query.Value, DmartJsonContext.Default.Query);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "client_join failed to deserialize sub-query for alias {Alias}", joinItem.Alias);
                continue;
            }
            if (subQuery is null) continue;
            int? userLimit = subQuery.Limit > 0 ? subQuery.Limit : null;

            // Right/Outer joins MUST surface right records the base set never
            // references, so we can't narrow the sub-query by left-side
            // values. Left/Inner can use a narrowing search as an
            // optimization — but only when every base value is parser-safe
            // (no `|`, `:`, `*`, `[`, `(`, `)`, `"`, `@`, whitespace); a
            // tenant-controlled value carrying any of those would corrupt
            // the synthesized `@field:v1|v2` expression. When narrowing is
            // unsafe, we fall back to pulling with the user's search alone
            // and matching client-side (bounded by the 1000-row cap below).
            //
            // Performance note: Right/Outer are O(right-table-size) by
            // design — the only way to know "which right records nobody
            // referenced" is to enumerate the right side. The 1000-row cap
            // bounds the worst case but doesn't make this cheap. Callers
            // with large right tables (customers, users, etc.) should pair
            // Right/Outer with a selective `sub_query.search` so the
            // sub-query returns only the slice of the right side that
            // matters; otherwise expect a noticeable latency cost
            // independent of the base set's size.
            var joinType = joinItem.Type ?? JoinType.Left;
            var isRightOrOuter = joinType is JoinType.Right or JoinType.Outer;

            // Build narrowing terms for Left/Inner. For Right/Outer we leave
            // searchTerms empty so the sub-query returns the full right side.
            var searchTerms = new List<string>();
            var hasLeftValues = false;
            var canNarrow = !isRightOrOuter;
            if (canNarrow)
            {
                foreach (var (lPath, lArr, rPath, _) in parsedJoins)
                {
                    var leftValues = new HashSet<string>(StringComparer.Ordinal);
                    var pairHasUnsafe = false;
                    foreach (var br in baseRecords)
                    {
                        foreach (var v in GetValuesFromRecord(br, lPath, lArr))
                        {
                            if (v is null) continue;
                            var formatted = FormatValue(v);
                            if (!SearchExpressionParser.IsSafeForAlternationValue(formatted)) { pairHasUnsafe = true; break; }
                            leftValues.Add(formatted);
                        }
                        if (pairHasUnsafe) break;
                    }
                    if (pairHasUnsafe || leftValues.Count == 0)
                    {
                        canNarrow = false;
                        searchTerms.Clear();
                        if (leftValues.Count > 0) hasLeftValues = true;
                        break;
                    }
                    hasLeftValues = true;
                    searchTerms.Add($"@{rPath}:{string.Join('|', leftValues)}");
                }
            }
            // Probe for any left value when we couldn't narrow (Right/Outer,
            // or Left/Inner fell back). Used below to decide whether a
            // user-search-less Left/Inner pull is worth doing at all.
            if (!hasLeftValues && (isRightOrOuter || !canNarrow))
            {
                foreach (var br in baseRecords)
                {
                    if (hasLeftValues) break;
                    foreach (var (lPath, lArr, _, _) in parsedJoins)
                    {
                        var found = false;
                        foreach (var v in GetValuesFromRecord(br, lPath, lArr))
                            if (v is not null) { found = true; break; }
                        if (found) { hasLeftValues = true; break; }
                    }
                }
            }

            // Fetch the sub-query unless there's nothing to match and no
            // unmatched-rights to surface either — i.e. Left/Inner with no
            // base values and no user filter is a no-op.
            var shouldFetch = isRightOrOuter
                || (canNarrow && searchTerms.Count > 0)
                || hasLeftValues
                || !string.IsNullOrEmpty(subQuery.Search);

            List<Record> rightRecords = new();
            if (shouldFetch)
            {
                string? combinedSearch;
                if (canNarrow && searchTerms.Count > 0)
                {
                    var injectedSearch = string.Join(' ', searchTerms);
                    // Concatenating with a user-supplied Search only AND's
                    // when the user search has no top-level parens — paren'd
                    // groups OR with the injected term (see ParseSearchExpression).
                    // Callers using paren'd sub-query filters need to express
                    // their own narrowing.
                    combinedSearch = string.IsNullOrEmpty(subQuery.Search)
                        ? injectedSearch
                        : $"{subQuery.Search} {injectedSearch}";
                }
                else
                {
                    combinedSearch = string.IsNullOrEmpty(subQuery.Search) ? null : subQuery.Search;
                }

                // Python caps sub-query at 1000 so a single join pull can't
                // blow up memory when the base set is wide. Sub-query's
                // jq_filter applies to the join's matched_list below (wrapped
                // with map() for vectorization), not to the sub-query's own
                // records — strip it before recursing.
                var widened = subQuery with
                {
                    Search = combinedSearch,
                    Limit = 1000,
                    JqFilter = null,
                    Join = null,
                    // The join code only consumes Records (line 602-603); the
                    // page-and-count run in parallel inside ExecuteAsync, so a
                    // null/true RetrieveTotal here doubles the SQL work for a
                    // total nobody reads. Pin to false.
                    RetrieveTotal = false,
                };
                var subResponse = await ExecuteAsync(widened, actor, ct);
                if (subResponse.Status == Status.Success && subResponse.Records is not null)
                    rightRecords = subResponse.Records;
            }

            // Index the right records by the FIRST parsed join pair's right
            // path — matches Python's approach exactly.
            var (lPath0, lArr0, rPath0, rArr0) = parsedJoins[0];
            var rightIndex = new Dictionary<string, List<Record>>(StringComparer.Ordinal);
            foreach (var rr in rightRecords)
            {
                foreach (var v in GetValuesFromRecord(rr, rPath0, rArr0))
                {
                    if (v is null) continue;
                    var key = FormatValue(v);
                    if (!rightIndex.TryGetValue(key, out var list))
                        rightIndex[key] = list = new List<Record>();
                    list.Add(rr);
                }
            }

            // For each base record, collect candidates, dedup, apply
            // additional parsed-join intersections, apply the sub-query's
            // user-supplied limit. Stash in matchedByBase so the jq_filter
            // (if set) can batch-process the whole join in one subprocess —
            // matches Python's per-join vectorized invocation.
            var matchedByBase = new List<List<Record>>(baseRecords.Count);
            for (var i = 0; i < baseRecords.Count; i++)
            {
                var br = baseRecords[i];
                var candidates = new List<Record>();
                foreach (var v in GetValuesFromRecord(br, lPath0, lArr0))
                {
                    if (v is null) continue;
                    if (rightIndex.TryGetValue(FormatValue(v), out var list))
                        candidates.AddRange(list);
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var unique = new List<Record>();
                foreach (var c in candidates)
                {
                    var uid = $"{c.Subpath}:{c.Shortname}:{JsonbHelpers.EnumMember(c.ResourceType)}";
                    if (seen.Add(uid)) unique.Add(c);
                }

                var matched = new List<Record>();
                foreach (var cand in unique)
                {
                    var allMatch = true;
                    for (var j = 1; j < parsedJoins.Count; j++)
                    {
                        var (lP, lA, rP, rA) = parsedJoins[j];
                        var lVs = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var v in GetValuesFromRecord(br, lP, lA))
                            if (v is not null) lVs.Add(FormatValue(v));
                        var rVs = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var v in GetValuesFromRecord(cand, rP, rA))
                            if (v is not null) rVs.Add(FormatValue(v));
                        if (!lVs.Overlaps(rVs)) { allMatch = false; break; }
                    }
                    if (allMatch) matched.Add(cand);
                }

                if (userLimit is int ul && matched.Count > ul)
                    matched = matched.GetRange(0, ul);

                matchedByBase.Add(matched);
            }

            // Python parity (adapter.py:1803-1872): when sub_query carries a
            // jq_filter, pipe the batched matched-lists through jq wrapped in
            // `map( [ <filter> ] )`. On success each base record's entry in the
            // "join" dict becomes the jq-transformed JSON (arbitrary shape).
            // On failure we surface JQ_TIMEOUT / JQ_ERROR as a query failure
            // rather than silently dropping the filter.
            object?[] joinPayloads = new object?[baseRecords.Count];
            if (!string.IsNullOrWhiteSpace(subQuery.JqFilter))
            {
                // Python parity (adapter.py:1817): vectorize with
                // `map( [ <expr> ] )` so one jq invocation handles the whole
                // list-of-lists input in one pass. The extra inner `[ ]` is
                // join-specific: it aggregates per-slice output back into an
                // array so the top-level result stays aligned 1:1 with
                // matchedByBase. Without it, `.[] | {...}` would iterate the
                // OUTER list and try to index an inner list with a string key
                // ("Cannot index array with string").
                var wrappedFilter = $"map( [ {subQuery.JqFilter} ] )";
                var inputBytes = SerializeMatchedForJq(matchedByBase);
                var jqResult = await JqRunner.RunAsync(
                    wrappedFilter, inputBytes, settings.Value.JqTimeout, ct);

                if (jqResult.Failure != JqRunner.FailureKind.None)
                    return (baseRecords, JqRunner.ToFailureResponse(jqResult.Failure, jqResult.Stderr));

                // Python wraps with `map( [ <expr> ] )` so the output is an
                // outer array aligned 1-to-1 with matchedByBase. Split it and
                // hand each base record its own slice.
                if (jqResult.Output is JsonElement outEl && outEl.ValueKind == JsonValueKind.Array)
                {
                    var idx = 0;
                    foreach (var slice in outEl.EnumerateArray())
                    {
                        if (idx >= joinPayloads.Length) break;
                        joinPayloads[idx++] = slice.Clone();
                    }
                }
            }

            // Decide which base records survive this join and which (if any)
            // unmatched right records get appended, per JoinQuery.Type:
            //   left  — keep every base record (default, original behavior)
            //   inner — drop base records with zero matches
            //   right — drop unmatched base AND append rights nobody matched
            //   outer — keep every base AND append rights nobody matched
            // Appended right records carry an empty matched-list under the
            // alias so downstream consumers see a consistent shape; you can
            // tell them apart from "unmatched left" by comparing the record's
            // own Subpath/Shortname against the sub-query's subpath.
            var dropUnmatchedBase = joinType is JoinType.Inner or JoinType.Right;
            var appendUnmatchedRights = joinType is JoinType.Right or JoinType.Outer;

            var survivors = new List<Record>(baseRecords.Count);
            for (var i = 0; i < baseRecords.Count; i++)
            {
                if (dropUnmatchedBase && matchedByBase[i].Count == 0) continue;
                var attrs = baseRecords[i].Attributes!;
                var joinDict = (Dictionary<string, object>)attrs["join"];
                joinDict[joinItem.Alias] = joinPayloads[i] ?? (object)matchedByBase[i];
                survivors.Add(baseRecords[i]);
            }

            if (appendUnmatchedRights)
            {
                var matchedRightUids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var matched in matchedByBase)
                    foreach (var rr in matched)
                        matchedRightUids.Add($"{rr.Subpath}:{rr.Shortname}:{JsonbHelpers.EnumMember(rr.ResourceType)}");

                foreach (var rr in rightRecords)
                {
                    var uid = $"{rr.Subpath}:{rr.Shortname}:{JsonbHelpers.EnumMember(rr.ResourceType)}";
                    if (!matchedRightUids.Add(uid)) continue;

                    // Always clone Attributes before mutating. The sub-query's
                    // record list (rightRecords) is local to this method today,
                    // but any future change that caches or shares it (paged
                    // result-set, retry buffer) would leak the join mutation
                    // back into the cache. Defensive-copy here is cheap
                    // (handful of unmatched-right records per join) and
                    // forecloses that bug class. The existing `["join"]` key,
                    // if any, is preserved when its value is a usable dict —
                    // but a non-dict value (e.g. a JsonElement that came in
                    // verbatim from a stored entry's body) gets replaced
                    // outright; a blind cast would throw and the outer catch
                    // at ExecuteAsync:113 would swallow the join into a
                    // silent un-joined 200.
                    var attrs = rr.Attributes is null
                        ? new Dictionary<string, object>(StringComparer.Ordinal)
                        : new Dictionary<string, object>(rr.Attributes, StringComparer.Ordinal);
                    var joinDict = attrs.TryGetValue("join", out var existingJoin)
                                   && existingJoin is Dictionary<string, object> ej
                        ? new Dictionary<string, object>(ej, StringComparer.Ordinal)
                        : new Dictionary<string, object>(StringComparer.Ordinal);
                    joinDict[joinItem.Alias] = new List<Record>();
                    // Caller-side discriminator: tag this record so consumers
                    // iterating Records can tell "appended unmatched right"
                    // apart from "base left" without subpath-string matching
                    // against the sub-query's subpath. The key is underscore-
                    // prefixed to make its synthetic origin obvious; consumers
                    // that don't care can ignore it.
                    joinDict["_join_origin"] = "right";
                    attrs["join"] = joinDict;
                    survivors.Add(rr with { Attributes = attrs });
                }
            }

            baseRecords.Clear();
            baseRecords.AddRange(survivors);
        }

        return (baseRecords, null);
    }

    // Serialize matchedByBase as an array-of-arrays of Record dicts, ready
    // to feed to `jq -c`. Mirrors Python's jq_dict_parser + json.dumps path
    // in adapter.py:1807-1816. We use the source-generated Record serializer
    // so the JSON shape matches dmart's wire format exactly.
    private static byte[] SerializeMatchedForJq(List<List<Record>> matchedByBase)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var inner in matchedByBase)
            {
                writer.WriteStartArray();
                foreach (var rec in inner)
                {
                    JsonSerializer.Serialize(writer, rec, DmartJsonContext.Default.Record);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return ms.ToArray();
    }

    // Parses a "l1:r1, l2:r2" expression into tuples. "[]" suffix on either
    // side signals that the field is expected to be a list (array hint).
    private static List<(string lPath, bool lArr, string rPath, bool rArr)> ParseJoinOn(string expr)
    {
        var result = new List<(string, bool, string, bool)>();
        foreach (var rawPart in expr.Split(','))
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue;
            var colon = part.IndexOf(':');
            if (colon < 0) throw new ArgumentException($"Invalid join_on expression: {expr}");
            var left = part[..colon].Trim();
            var right = part[(colon + 1)..].Trim();
            var lArr = left.EndsWith("[]", StringComparison.Ordinal);
            var rArr = right.EndsWith("[]", StringComparison.Ordinal);
            if (lArr) left = left[..^2];
            if (rArr) right = right[..^2];
            result.Add((left, lArr, right, rArr));
        }
        return result;
    }

    // Pulls values from a Record at the given dotted path. Matches Python's
    // get_values_from_record behavior: top-level Record fields come from the
    // strongly-typed properties; everything else walks record.attributes.
    private static List<object?> GetValuesFromRecord(Record rec, string path, bool arrayHint)
    {
        try
        {
            object? val = path switch
            {
                "shortname" => rec.Shortname,
                "resource_type" => JsonbHelpers.EnumMember(rec.ResourceType),
                "subpath" => rec.Subpath,
                "uuid" => rec.Uuid,
                "space_name" => rec.Attributes is { } a && a.TryGetValue("space_name", out var sp) ? sp : null,
                _ => GetNestedFromAttributes(rec.Attributes, path),
            };
            if (val is null) return new();
            // A list value short-circuits the array_hint — we always unpack
            // to scalar primitives, dropping nested dicts/records.
            if (val is System.Collections.IList list && val is not string)
            {
                var outList = new List<object?>();
                foreach (var item in list)
                {
                    if (item is null or string or bool) outList.Add(item);
                    else if (item.GetType().IsPrimitive || item is decimal) outList.Add(item);
                    else if (item is JsonElement je && (je.ValueKind is JsonValueKind.String
                        or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                        outList.Add(JsonElementToObject(je));
                }
                return outList;
            }
            return new() { val };
        }
        catch { return new(); }
    }

    private static object? GetNestedFromAttributes(Dictionary<string, object>? attrs, string path)
    {
        if (attrs is null) return null;
        var segments = path.Split('.');
        object? current = attrs;
        foreach (var seg in segments)
        {
            if (current is null) return null;
            current = current switch
            {
                // Dictionary<string, object?> and Dictionary<string, object> are the
                // same CLR type (nullable annotations are erased), so one case
                // covers both EntryMapper's output and Record.Attributes.
                Dictionary<string, object> d => d.TryGetValue(seg, out var v) ? v : null,
                Payload p => seg == "body" ? (object?)p.Body : null,
                JsonElement je when je.ValueKind == JsonValueKind.Object =>
                    je.TryGetProperty(seg, out var child) ? JsonElementToObject(child) : null,
                _ => null,
            };
        }
        return current;
    }

    private static object? JsonElementToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var i) ? i : (object)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => ExtractJsonArray(e),
        JsonValueKind.Object => e,  // keep as JsonElement so caller can drill further
        _ => null,
    };

    private static List<object?> ExtractJsonArray(JsonElement e)
    {
        var outList = new List<object?>();
        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False)
                outList.Add(JsonElementToObject(item));
        }
        return outList;
    }

    // Formats a value as its string key for the left/right value index.
    // Matches Python's `str(val)` behavior.
    private static string FormatValue(object v) => v switch
    {
        string s => s,
        bool b => b ? "True" : "False",  // Python str(True) == "True"
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    // ====================================================================
    // FILTER FIELDS VALUES (row-level ACL via search-clause merge)
    // ====================================================================
    // Mirrors Python's adapter.py:1529-1566 — every permission can carry a
    // `filter_fields_values` string (RediSearch-style `@field:value` clause)
    // that further restricts which rows the holder is allowed to see. We
    // build the per-permission map, intersect it with the request's
    // resolved query policies, and merge the resulting clauses into
    // q.Search alongside the @space_name / @subpath / @resource_type
    // restriction so the SQL layer narrows the page accordingly.
    //
    // TRUST MODEL: filter_fields_values strings are admin-managed input —
    // they come from rows in the `permissions` table (only writable by
    // admin paths) and are concatenated verbatim into the search clause.
    // Any future code path that lets a non-admin influence a permission
    // row's filter_fields_values would open a search-clause-injection
    // hole. Don't add such a path without escaping.
    //
    // Per-request cost: GenerateUserPermissionsAsync hits userpermissionscache
    // (single indexed SELECT on user_shortname). The role-walk cold path is
    // rare; the warm path is one PG round-trip. No in-memory cache layer
    // sits above it — high-QPS deployments could add one if this becomes
    // a profile hot spot.
    private async Task<Query> MergeFilterFieldsValuesAsync(
        Query q, List<string> policies, string? actor, CancellationToken ct)
    {
        var userShortname = actor ?? PermissionService.AnonymousUser;
        var userPermissions = await access.GenerateUserPermissionsAsync(userShortname, ct);
        return MergeFilterFieldsValues(q, policies, userPermissions);
    }

    // Pure logic split out of MergeFilterFieldsValuesAsync so it's
    // testable without spinning up an AccessRepository / DB.
    internal static Query MergeFilterFieldsValues(
        Query q, List<string> policies, Dictionary<string, object> userPermissions)
    {
        if (userPermissions.Count == 0) return DedupeSearchTokens(q);

        // Mirror adapter.py: filtered_policies subset by space:subpath
        // (and filter_types when present).
        var subpathTarget = string.IsNullOrEmpty(q.Subpath) || q.Subpath == "/"
            ? "/" : q.Subpath.TrimStart('/');

        List<string> filteredPolicies;
        if (q.FilterTypes is { Count: > 0 } fts)
        {
            // PYTHON-BUG PARITY: upstream's loop reassigns `filtered_policies`
            // on each iteration instead of accumulating, so only the LAST
            // filter_type's matches contribute the FFV restriction. A query
            // for FilterTypes=[User, Role] applies the Role permission's FFV
            // only — the User permission's FFV is dropped on the floor.
            // We mirror that here for wire compatibility; release notes call
            // it out so consumers don't structure queries assuming the
            // logical-union behavior they'd expect.
            // TODO(parity): file an upstream fix; once Python lands the
            // accumulating version, switch to .Concat() here in lockstep.
            filteredPolicies = new();
            foreach (var ft in fts)
            {
                var rtStr = JsonbHelpers.EnumMember(ft);
                var target = $"{q.SpaceName}:{subpathTarget}:{rtStr}";
                filteredPolicies = policies
                    .Where(p => p.StartsWith(target, StringComparison.Ordinal))
                    .ToList();
            }
        }
        else
        {
            var target = $"{q.SpaceName}:{subpathTarget}";
            filteredPolicies = policies
                .Where(p => p.StartsWith(target, StringComparison.Ordinal))
                .ToList();
        }

        var ffvSpaces = new List<string>();
        var ffvSubpaths = new List<string>();
        var ffvResourceTypes = new List<string>();
        var ffvQuery = new List<string>();

        foreach (var policy in filteredPolicies)
        {
            foreach (var (permKey, permEntry) in userPermissions)
            {
                // BuildUserQueryPoliciesAsync emits policies with the subpath
                // segment TrimStart('/')'d, but GenerateUserPermissionsAsync
                // stores whatever was in `permissions.subpaths` — typically
                // WITH a leading slash. Without normalisation a permission
                // stored as "/sp" never matches a policy keyed "sp", and
                // FFV silently no-ops. Try the raw permKey first (preserves
                // any deployment whose storage already aligns), then the
                // slash-stripped form. The @subpath:/{…} clause below
                // emits the normalised value so the SQL parser sees a
                // single canonical leading slash regardless of storage.
                var keyParts = permKey.Split(':');
                if (keyParts.Length < 3) continue;
                var permSubpathNorm = keyParts[1].TrimStart('/');
                var normalizedKey = $"{keyParts[0]}:{permSubpathNorm}:{keyParts[2]}";
                var matches = policy.StartsWith(permKey, StringComparison.Ordinal)
                    || policy.StartsWith(normalizedKey, StringComparison.Ordinal);
                if (!matches) continue;

                var ffv = ExtractFilterFieldsValues(permEntry);
                if (string.IsNullOrEmpty(ffv)) continue;

                if (!ffvQuery.Contains(ffv)) ffvQuery.Add(ffv);
                ffvSpaces.Add(keyParts[0]);
                ffvSubpaths.Add(permSubpathNorm);
                ffvResourceTypes.Add(keyParts[2]);
            }
        }

        if (ffvSpaces.Count == 0) return DedupeSearchTokens(q);

        var permKeyQuery =
            $"@space_name:{string.Join("|", ffvSpaces)} " +
            $"@subpath:/{string.Join("|/", ffvSubpaths)} " +
            $"@resource_type:{string.Join("|", ffvResourceTypes)} " +
            string.Join(" ", ffvQuery);

        var newSearch = string.IsNullOrEmpty(q.Search)
            ? permKeyQuery
            : $"{q.Search} {permKeyQuery}";

        return DedupeSearchTokens(q with { Search = newSearch });
    }

    // Tokenises q.Search on plain ASCII space and dedupes — good enough
    // for the @field:value tokens dmart's permission FFV strings actually
    // produce, none of which contain whitespace. RediSearch's quoted
    // alternative — `@field:"hello world"` — would split incorrectly here,
    // but that shape is not generated anywhere in the FFV path; if it ever
    // is, this function needs a quote-aware tokenizer.
    internal static Query DedupeSearchTokens(Query q)
    {
        if (string.IsNullOrEmpty(q.Search)) return q;
        var parts = q.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<string>(parts.Length);
        foreach (var p in parts)
            if (seen.Add(p)) deduped.Add(p);
        if (deduped.Count == parts.Length) return q;
        return q with { Search = string.Join(' ', deduped) };
    }

    // user_permissions values come either as Dictionary<string,object>
    // (freshly built by GenerateUserPermissionsAsync) or as JsonElement
    // (when round-tripped through the userpermissionscache JSONB column).
    // Handle both.
    internal static string? ExtractFilterFieldsValues(object permEntry)
    {
        switch (permEntry)
        {
            case Dictionary<string, object> dict
                when dict.TryGetValue("filter_fields_values", out var v):
                return v switch
                {
                    string s => string.IsNullOrEmpty(s) ? null : s,
                    JsonElement je when je.ValueKind == JsonValueKind.String
                        => je.GetString() is { Length: > 0 } gs ? gs : null,
                    _ => null,
                };
            case JsonElement je when je.ValueKind == JsonValueKind.Object
                && je.TryGetProperty("filter_fields_values", out var v2)
                && v2.ValueKind == JsonValueKind.String:
                return v2.GetString() is { Length: > 0 } s2 ? s2 : null;
            default:
                return null;
        }
    }
}

// ========================================================================
// MAPPERS — project DB models → Record for the wire
// ========================================================================
// Python's to_record() dumps every __dict__ key minus the "local props"
// (uuid, resource_type, shortname, subpath). Then _set_query_final_results
// deletes password (for users) and query_policies (for all). We mirror that.

// Strip null-valued and empty-string entries from the attributes dictionary so the
// JSON response doesn't contain "key": null or "key": "" for optional fields.
internal static class AttrHelper
{
    public static Dictionary<string, object> StripNulls(Dictionary<string, object?> attrs)
    {
        var result = new Dictionary<string, object>(attrs.Count, StringComparer.Ordinal);
        foreach (var (k, v) in attrs)
        {
            if (v is null) continue;
            if (v is string s && s.Length == 0) continue;
            result[k] = v;
        }
        return result;
    }
}

internal static class EntryMapper
{
    public static Record ToRecord(Entry e, string spaceName, bool includePayloadBody = true)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = e.IsActive,
            ["slug"] = e.Slug,
            ["displayname"] = e.Displayname,
            ["description"] = e.Description,
            ["tags"] = e.Tags,
            ["created_at"] = e.CreatedAt,
            ["updated_at"] = e.UpdatedAt,
            ["owner_shortname"] = e.OwnerShortname,
            ["owner_group_shortname"] = e.OwnerGroupShortname,
            // acl / relationships are always emitted — null collapses to an
            // empty array so clients get a stable shape (Python does the same).
            ["acl"] = e.Acl ?? new List<AclEntry>(),
            ["payload"] = includePayloadBody ? e.Payload : StripPayloadBody(e.Payload),
            ["relationships"] = e.Relationships ?? new List<Dictionary<string, object>>(),
            ["last_checksum_history"] = e.LastChecksumHistory,
            ["state"] = e.State,
            ["is_open"] = e.IsOpen,
            ["reporter"] = e.Reporter,
            ["workflow_shortname"] = e.WorkflowShortname,
            ["collaborators"] = e.Collaborators,
            ["resolution_reason"] = e.ResolutionReason,
            ["space_name"] = spaceName,
        };
        return new Record
        {
            ResourceType = e.ResourceType,
            Subpath = e.Subpath,
            Shortname = e.Shortname,
            Uuid = e.Uuid,
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }

    private static Payload? StripPayloadBody(Payload? p)
    {
        if (p is null) return null;
        return p with { Body = null };
    }
}

internal static class SpaceMapper
{
    public static Record ToRecord(Space s)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = s.IsActive,
            ["slug"] = s.Slug,
            ["displayname"] = s.Displayname,
            ["description"] = s.Description,
            ["tags"] = s.Tags,
            ["created_at"] = s.CreatedAt,
            ["updated_at"] = s.UpdatedAt,
            ["owner_shortname"] = s.OwnerShortname,
            ["owner_group_shortname"] = s.OwnerGroupShortname,
            // Always-present shape keys — null collapses to empty collection.
            ["acl"] = s.Acl ?? new List<AclEntry>(),
            ["payload"] = s.Payload,
            ["relationships"] = s.Relationships ?? new List<Dictionary<string, object>>(),
            ["last_checksum_history"] = s.LastChecksumHistory,
            ["root_registration_signature"] = s.RootRegistrationSignature,
            ["primary_website"] = s.PrimaryWebsite,
            ["indexing_enabled"] = s.IndexingEnabled,
            ["capture_misses"] = s.CaptureMisses,
            ["check_health"] = s.CheckHealth,
            ["languages"] = s.Languages,
            ["icon"] = s.Icon,
            ["mirrors"] = s.Mirrors,
            ["hide_folders"] = s.HideFolders,
            ["hide_space"] = s.HideSpace,
            ["ordinal"] = s.Ordinal,
            ["space_name"] = s.SpaceName,
        };
        return new Record
        {
            ResourceType = ResourceType.Space,
            Subpath = s.Subpath,
            Shortname = s.Shortname,
            Uuid = s.Uuid,
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }
}

internal static class UserMapper
{
    public static Record ToRecord(User u)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = u.IsActive,
            ["slug"] = u.Slug,
            ["displayname"] = u.Displayname,
            ["description"] = u.Description,
            ["tags"] = u.Tags,
            ["created_at"] = u.CreatedAt,
            ["updated_at"] = u.UpdatedAt,
            ["owner_shortname"] = u.OwnerShortname,
            ["owner_group_shortname"] = u.OwnerGroupShortname,
            ["acl"] = u.Acl,
            ["payload"] = u.Payload,
            ["relationships"] = u.Relationships,
            ["last_checksum_history"] = u.LastChecksumHistory,
            // User-specific (password deliberately excluded — Python strips it)
            ["roles"] = u.Roles,
            ["groups"] = u.Groups,
            ["type"] = JsonbHelpers.EnumMember(u.Type),
            ["language"] = JsonbHelpers.EnumMember(u.Language),
            ["email"] = u.Email,
            ["msisdn"] = u.Msisdn,
            ["locked_to_device"] = u.LockedToDevice,
            ["is_email_verified"] = u.IsEmailVerified,
            ["is_msisdn_verified"] = u.IsMsisdnVerified,
            ["force_password_change"] = u.ForcePasswordChange,
            ["device_id"] = u.DeviceId,
            ["google_id"] = u.GoogleId,
            ["facebook_id"] = u.FacebookId,
            ["apple_id"] = u.AppleId,
            ["social_avatar_url"] = u.SocialAvatarUrl,
            ["notes"] = u.Notes,
            ["space_name"] = u.SpaceName,
        };
        return new Record
        {
            ResourceType = ResourceType.User,
            Subpath = u.Subpath,
            Shortname = u.Shortname,
            Uuid = u.Uuid,
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }
}

internal static class GroupMapper
{
    public static Record ToRecord(Group g)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = g.IsActive,
            ["slug"] = g.Slug,
            ["displayname"] = g.Displayname,
            ["description"] = g.Description,
            ["tags"] = g.Tags,
            ["created_at"] = g.CreatedAt,
            ["updated_at"] = g.UpdatedAt,
            ["owner_shortname"] = g.OwnerShortname,
            ["owner_group_shortname"] = g.OwnerGroupShortname,
            ["acl"] = g.Acl,
            ["payload"] = g.Payload,
            ["relationships"] = g.Relationships,
            ["last_checksum_history"] = g.LastChecksumHistory,
            ["grantable_by"] = g.GrantableBy,
            ["space_name"] = g.SpaceName,
        };
        return new Record
        {
            ResourceType = ResourceType.Group,
            Subpath = g.Subpath,
            Shortname = g.Shortname,
            Uuid = g.Uuid,
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }
}

internal static class RoleMapper
{
    public static Record ToRecord(Role r)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = r.IsActive,
            ["slug"] = r.Slug,
            ["displayname"] = r.Displayname,
            ["description"] = r.Description,
            ["tags"] = r.Tags,
            ["created_at"] = r.CreatedAt,
            ["updated_at"] = r.UpdatedAt,
            ["owner_shortname"] = r.OwnerShortname,
            ["owner_group_shortname"] = r.OwnerGroupShortname,
            ["acl"] = r.Acl,
            ["payload"] = r.Payload,
            ["relationships"] = r.Relationships,
            ["last_checksum_history"] = r.LastChecksumHistory,
            ["permissions"] = r.Permissions,
            ["space_name"] = r.SpaceName,
        };
        return new Record
        {
            ResourceType = ResourceType.Role,
            Subpath = r.Subpath,
            Shortname = r.Shortname,
            Uuid = r.Uuid,
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }
}

internal static class PermissionMapper
{
    public static Record ToRecord(Permission p)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = p.IsActive,
            ["slug"] = p.Slug,
            ["displayname"] = p.Displayname,
            ["description"] = p.Description,
            ["tags"] = p.Tags,
            ["created_at"] = p.CreatedAt,
            ["updated_at"] = p.UpdatedAt,
            ["owner_shortname"] = p.OwnerShortname,
            ["owner_group_shortname"] = p.OwnerGroupShortname,
            ["acl"] = p.Acl,
            ["payload"] = p.Payload,
            ["relationships"] = p.Relationships,
            ["last_checksum_history"] = p.LastChecksumHistory,
            ["subpaths"] = p.Subpaths,
            ["resource_types"] = p.ResourceTypes,
            ["actions"] = p.Actions,
            ["conditions"] = p.Conditions,
            ["restricted_fields"] = p.RestrictedFields,
            ["allowed_fields_values"] = p.AllowedFieldsValues,
            ["filter_fields_values"] = p.FilterFieldsValues,
            ["space_name"] = p.SpaceName,
        };
        return new Record
        {
            ResourceType = ResourceType.Permission,
            Subpath = p.Subpath,
            Shortname = p.Shortname,
            Uuid = p.Uuid,
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }
}

internal static class AttachmentMapper
{
    public static Record ToRecord(Attachment a)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = a.IsActive,
            ["slug"] = a.Slug,
            ["displayname"] = a.Displayname,
            ["description"] = a.Description,
            ["tags"] = a.Tags,
            ["created_at"] = a.CreatedAt,
            ["updated_at"] = a.UpdatedAt,
            ["owner_shortname"] = a.OwnerShortname,
            ["owner_group_shortname"] = a.OwnerGroupShortname,
            ["acl"] = a.Acl,
            ["payload"] = a.Payload,
            ["relationships"] = a.Relationships,
            ["last_checksum_history"] = a.LastChecksumHistory,
            ["body"] = a.Body,
            ["state"] = a.State,
            ["space_name"] = a.SpaceName,
        };
        return new Record
        {
            ResourceType = a.ResourceType,
            Subpath = a.Subpath,
            Shortname = a.Shortname,
            Uuid = a.Uuid,
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }

    // Mirrors Python's get_entry_attachments output shape. The attachment is
    // returned as a child of its parent entry, so:
    //   * subpath is trimmed to the parent's subpath (last segment removed)
    //   * media, relationships, acl, space_name are excluded from attributes
    public static Record ToEntryRecord(Attachment a)
    {
        // Python: "/".join(subpath.split("/")[:-1])  — strip trailing shortname segment
        var parentSubpath = a.Subpath;
        var lastSlash = parentSubpath.LastIndexOf('/');
        if (lastSlash > 0)
            parentSubpath = parentSubpath[..lastSlash];
        else
            parentSubpath = "/";

        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_active"] = a.IsActive,
            ["slug"] = a.Slug,
            ["displayname"] = a.Displayname,
            ["description"] = a.Description,
            ["tags"] = a.Tags,
            ["created_at"] = a.CreatedAt,
            ["updated_at"] = a.UpdatedAt,
            ["owner_shortname"] = a.OwnerShortname,
            ["owner_group_shortname"] = a.OwnerGroupShortname,
            ["payload"] = a.Payload,
            ["last_checksum_history"] = a.LastChecksumHistory,
            ["body"] = a.Body,
            ["state"] = a.State,
        };
        return new Record
        {
            ResourceType = a.ResourceType,
            Subpath = parentSubpath,
            Shortname = a.Shortname,
            Uuid = a.Uuid,
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }
}

internal static class HistoryMapper
{
    public static Record ToRecord(HistoryRecord h)
    {
        // Python-parity diff transform (adapter.py:3101-3111):
        //   - for any diff key "password", replace old/new with "********"
        //   - for every other key, if old/new is a dict, pop "headers" from it
        // Walk structurally so nested {old,new} pairs are handled correctly —
        // the previous regex pass only caught top-level "password":"string"
        // values and missed the actual history shape.
        JsonElement? diffElem = null;
        if (h.Diff is not null)
        {
            using var doc = JsonDocument.Parse(h.Diff);
            diffElem = TransformHistoryDiff(doc.RootElement);
        }

        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner_shortname"] = h.OwnerShortname,
            ["timestamp"] = h.Timestamp,
            ["diff"] = diffElem,
            ["last_checksum_history"] = h.LastChecksumHistory,
            ["space_name"] = h.SpaceName,
        };
        return new Record
        {
            ResourceType = ResourceType.History,
            Shortname = h.Shortname,
            Subpath = h.Subpath,
            Uuid = h.Uuid.ToString(),
            Attributes = AttrHelper.StripNulls(attrs),
        };
    }

    // Walks a diff JsonElement and returns a transformed copy that mirrors
    // Python's in-place mutation loop. Only produces a new tree when changes
    // are needed — otherwise returns the cloned original.
    private static JsonElement TransformHistoryDiff(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root.Clone();

        var outNode = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in root.EnumerateObject())
        {
            // Python's adapter.py:3101-3111 masks only the top-level `"password"`
            // key. We go one step further: also mask flattened-dotted keys like
            // `payload.body.password` so a password nested inside an Entry's
            // payload body cannot leak via /managed/query?type=history. This
            // one-line divergence from Python is deliberate defense-in-depth.
            if (prop.Name == "password"
                || prop.Name.EndsWith(".password", StringComparison.Ordinal))
            {
                outNode[prop.Name] = new System.Text.Json.Nodes.JsonObject
                {
                    ["old"] = "********",
                    ["new"] = "********",
                };
                continue;
            }
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                // Drop `headers` from each `old`/`new` nested dict.
                var changeNode = new System.Text.Json.Nodes.JsonObject();
                foreach (var state in prop.Value.EnumerateObject())
                {
                    if (state.Name is "old" or "new" && state.Value.ValueKind == JsonValueKind.Object)
                    {
                        var scrubbed = new System.Text.Json.Nodes.JsonObject();
                        foreach (var kv in state.Value.EnumerateObject())
                        {
                            if (kv.Name == "headers") continue;
                            scrubbed[kv.Name] = ElementToNode(kv.Value);
                        }
                        changeNode[state.Name] = scrubbed;
                    }
                    else
                    {
                        changeNode[state.Name] = ElementToNode(state.Value);
                    }
                }
                outNode[prop.Name] = changeNode;
            }
            else
            {
                outNode[prop.Name] = ElementToNode(prop.Value);
            }
        }
        // Serialize the node tree straight to UTF-8 bytes and reparse — avoids
        // the UTF-16 string detour of ToJsonString() → Parse().
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            outNode.WriteTo(writer);
        using var doc2 = JsonDocument.Parse(buffer.WrittenMemory);
        return doc2.RootElement.Clone();
    }

    // JsonElement → JsonNode without round-tripping through a string. The BCL
    // Create() factories clone the element's tokens directly and are AOT-safe
    // (no serializer metadata). JSON null maps to a null node, matching the
    // prior JsonNode.Parse("null") behavior.
    private static System.Text.Json.Nodes.JsonNode? ElementToNode(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => System.Text.Json.Nodes.JsonObject.Create(el),
        JsonValueKind.Array => System.Text.Json.Nodes.JsonArray.Create(el),
        JsonValueKind.Null => null,
        _ => System.Text.Json.Nodes.JsonValue.Create(el),
    };
}
