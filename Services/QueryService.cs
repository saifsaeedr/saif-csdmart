using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Utils;
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

    public async Task<Response> ExecuteAsync(Query q, string? actor, CancellationToken ct = default)
    {
        // Clamp limit: default to 100, cap at MaxQueryLimit.
        var maxLimit = settings.Value.MaxQueryLimit;
        var limit = q.Limit <= 0 ? 100 : q.Limit;
        if (maxLimit > 0 && limit > maxLimit) limit = maxLimit;
        q = q with { Limit = limit };

        if (string.IsNullOrEmpty(q.SpaceName))
            return Response.Fail(InternalErrorCode.INVALID_DATA, "space_name is required", ErrorTypes.Request);

        var response = q.Type switch
        {
            QueryType.Spaces => await QuerySpacesAsync(q, actor, ct),
            QueryType.History => await QueryHistoryAsync(q, actor, ct),
            QueryType.Attachments => await QueryAttachmentsAsync(q, actor, ct),
            QueryType.Tags => await QueryTagsAsync(q, actor, ct),
            QueryType.Aggregation => await QueryAggregationAsync(q, actor, "entries", ct),
            QueryType.AttachmentsAggregation => await QueryAggregationAsync(q, actor, "attachments", ct),
            QueryType.Counters => await QueryCountersAsync(q, actor, ct),
            QueryType.Events => Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                "events query is Python-only (reads spaces_folder/<space>/.dm/events.jsonl from disk); the C# port keeps all data in PostgreSQL",
                ErrorTypes.Request),
            _ => await DispatchTableQuery(q, actor, ct),
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
            try
            {
                var (joined, jqFail) = await ApplyClientJoinsAsync(records, joins, actor, ct);
                if (jqFail is not null)
                    // Python propagates jq failures up as HTTP 400. Match that —
                    // the client asked for a filter and we couldn't honor it, so
                    // returning un-filtered data would be misleading.
                    return jqFail;
                response = response with { Records = joined };
            }
            catch (Exception ex)
            {
                // Python swallows non-jq join errors with a print; match that:
                // return the un-joined base results rather than failing the
                // whole query. jq failures are already handled above via the
                // tuple return — this catch is for join-algorithm bugs only.
                logger.LogWarning(ex, "client_join join failed");
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

        var pageTask = users.QueryAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : users.CountQueryAsync(q, ct);
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

        var pageTask = access.QueryRolesAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : access.CountRolesQueryAsync(q, ct);
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask).Select(RoleMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // PERMISSIONS (management/permissions)
    // ====================================================================

    private async Task<Response> QueryPermissionsAsync(Query q, string? actor, CancellationToken ct)
    {
        if (!await CanQueryAsync(actor, ResourceType.Permission, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        var pageTask = access.QueryPermissionsAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : access.CountPermissionsQueryAsync(q, ct);
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
    // TAGS (SQL aggregation)
    // ====================================================================

    private async Task<Response> QueryTagsAsync(Query q, string? actor, CancellationToken ct)
    {
        if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        // SQL: unnest tags jsonb array, group by tag, count.
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        var sql = new System.Text.StringBuilder($"""
            SELECT tag, COUNT(*) AS cnt
            FROM entries, jsonb_array_elements_text(tags) AS tag
            WHERE {where}
            GROUP BY tag ORDER BY cnt DESC
            """);
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
        if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        // Row-level ACL: mirrors Python's get_user_query_policies + apply.
        // Empty policies for an authenticated actor → the gate passed on
        // some wildcard but no grant reaches this subpath. Python returns
        // (0, []); match that so the caller can't infer row counts.
        List<string>? policies = null;
        if (actor is not null)
        {
            policies = await perms.BuildUserQueryPoliciesAsync(actor, q.SpaceName, q.Subpath ?? "/", ct);
            if (policies.Count == 0) return EmptyQueryResponse();
        }

        var pageTask = actor is not null
            ? entries.QueryAsync(q, actor, policies, ct)
            : entries.QueryAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : (actor is not null
                ? entries.CountQueryAsync(q, actor, policies, ct)
                : entries.CountQueryAsync(q, ct));
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask)
            .Select(e => EntryMapper.ToRecord(e, q.SpaceName, q.RetrieveJsonPayload))
            .ToList();

        // If retrieve_attachments, fetch and attach for each record.
        // Process in fixed-size batches rather than creating all N tasks upfront
        // so large result sets (e.g. 10k records at MaxQueryLimit) don't allocate
        // N pending Task objects — each batch of 5 runs concurrently then the
        // next batch starts. This also keeps DB connection-pool pressure bounded.
        if (q.RetrieveAttachments && records.Count > 0)
        {
            const int BatchSize = 5;
            for (var offset = 0; offset < records.Count; offset += BatchSize)
            {
                var end = Math.Min(offset + BatchSize, records.Count);
                var batchTasks = new Task<List<Models.Core.Attachment>>[end - offset];
                for (var j = offset; j < end; j++)
                {
                    var rec = records[j];
                    batchTasks[j - offset] = attachments.ListForParentAsync(
                        q.SpaceName, rec.Subpath, rec.Shortname, ct);
                }
                var batchResults = await Task.WhenAll(batchTasks);
                for (var j = offset; j < end; j++)
                {
                    var result = batchResults[j - offset];
                    if (result.Count > 0)
                    {
                        records[j] = records[j] with
                        {
                            Attachments = result
                                .GroupBy(a => DataAdapters.Sql.JsonbHelpers.EnumMember(a.ResourceType))
                                .ToDictionary(
                                    g => g.Key,
                                    g => g.Select(a => AttachmentMapper.ToEntryRecord(a)).ToList())
                        };
                    }
                }
            }
        }

        return Response.Ok(records, new() { ["total"] = await totalTask, ["returned"] = records.Count });
    }

    // ====================================================================
    // AGGREGATION (GROUP BY + reducers)
    // ====================================================================

    private async Task<Response> QueryAggregationAsync(Query q, string? actor, string tableName, CancellationToken ct)
    {
        if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
            return EmptyQueryResponse();

        if (q.AggregationData is null)
            return Response.Fail(InternalErrorCode.MISSING_DATA,
                "aggregation_data required for aggregation queries", ErrorTypes.Request);

        var rows = await QueryHelper.RunAggregationAsync(db, tableName, q, ct);

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
                total = await users.CountQueryAsync(q, ct);
            }
            else if (sub == "roles" || sub.StartsWith("roles/", StringComparison.Ordinal))
            {
                if (!await CanQueryAsync(actor, ResourceType.Role, q.SpaceName, q.Subpath ?? "/", ct))
                    return EmptyQueryResponse();
                total = await access.CountRolesQueryAsync(q, ct);
            }
            else if (sub == "permissions" || sub.StartsWith("permissions/", StringComparison.Ordinal))
            {
                if (!await CanQueryAsync(actor, ResourceType.Permission, q.SpaceName, q.Subpath ?? "/", ct))
                    return EmptyQueryResponse();
                total = await access.CountPermissionsQueryAsync(q, ct);
            }
            else
            {
                if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
                    return EmptyQueryResponse();
                total = await entries.CountQueryAsync(q, ct);
            }
        }
        else
        {
            if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
                return EmptyQueryResponse();
            if (actor is not null)
            {
                var policies = await perms.BuildUserQueryPoliciesAsync(actor, q.SpaceName, q.Subpath ?? "/", ct);
                if (policies.Count == 0) return EmptyQueryResponse();
                total = await entries.CountQueryAsync(q, actor, policies, ct);
            }
            else
            {
                total = await entries.CountQueryAsync(q, ct);
            }
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

            // Build per-pair search terms ("@right:val1|val2|...") from the
            // base records' left-path values.
            var searchTerms = new List<string>();
            var possibleMatch = true;
            foreach (var (lPath, lArr, rPath, _) in parsedJoins)
            {
                var leftValues = new HashSet<string>(StringComparer.Ordinal);
                foreach (var br in baseRecords)
                {
                    foreach (var v in GetValuesFromRecord(br, lPath, lArr))
                    {
                        if (v is not null) leftValues.Add(FormatValue(v));
                    }
                }
                if (leftValues.Count == 0) { possibleMatch = false; break; }
                searchTerms.Add($"@{rPath}:{string.Join('|', leftValues)}");
            }

            List<Record> rightRecords = new();
            if (possibleMatch)
            {
                var injectedSearch = string.Join(' ', searchTerms);
                var combinedSearch = string.IsNullOrEmpty(subQuery.Search)
                    ? injectedSearch
                    : $"{subQuery.Search} {injectedSearch}";
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

            // Assign per-base either the raw matched list (no filter) or the
            // jq output slice (filter applied).
            for (var i = 0; i < baseRecords.Count; i++)
            {
                var attrs = baseRecords[i].Attributes!;
                var joinDict = (Dictionary<string, object>)attrs["join"];
                joinDict[joinItem.Alias] = joinPayloads[i] ?? (object)matchedByBase[i];
            }
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
            ["active_plugins"] = s.ActivePlugins,
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
                            scrubbed[kv.Name] = System.Text.Json.Nodes.JsonNode.Parse(kv.Value.GetRawText());
                        }
                        changeNode[state.Name] = scrubbed;
                    }
                    else
                    {
                        changeNode[state.Name] = System.Text.Json.Nodes.JsonNode.Parse(state.Value.GetRawText());
                    }
                }
                outNode[prop.Name] = changeNode;
            }
            else
            {
                outNode[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
            }
        }
        using var doc2 = JsonDocument.Parse(outNode.ToJsonString());
        return doc2.RootElement.Clone();
    }
}
