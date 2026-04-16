using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
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
    IOptions<DmartSettings> settings)
{
    // Permission gate for query methods. Tries "view" first (works for anonymous +
    // authenticated), then "query" (some permissions only list "query" not "view").
    // For root queries where the user's permissions are keyed to specific subpaths
    // (not "/"), falls back to HasAnyAccessToSpaceAsync.
    private async Task<bool> CanQueryAsync(string? actor, ResourceType rt, string spaceName, string subpath, CancellationToken ct)
    {
        var probe = new Locator(rt, spaceName, subpath, "*");
        if (await perms.CanAsync(actor, "view", probe, ct: ct)) return true;
        if (actor is not null && await perms.CanAsync(actor, "query", probe, ct: ct)) return true;
        return subpath == "/" && await perms.HasAnyAccessToSpaceAsync(actor, spaceName, ct);
    }

    public async Task<Response> ExecuteAsync(Query q, string? actor, CancellationToken ct = default)
    {
        // Clamp limit: default to 100, cap at MaxQueryLimit.
        var maxLimit = settings.Value.MaxQueryLimit;
        var limit = q.Limit <= 0 ? 100 : q.Limit;
        if (maxLimit > 0 && limit > maxLimit) limit = maxLimit;
        q = q with { Limit = limit };

        if (string.IsNullOrEmpty(q.SpaceName))
            return Response.Fail("bad_query", "space_name is required");

        return q.Type switch
        {
            QueryType.Spaces => await QuerySpacesAsync(q, actor, ct),
            QueryType.History => await QueryHistoryAsync(q, actor, ct),
            QueryType.Attachments => await QueryAttachmentsAsync(q, actor, ct),
            QueryType.Tags => await QueryTagsAsync(q, actor, ct),
            QueryType.Aggregation => await QueryAggregationAsync(q, actor, "entries", ct),
            QueryType.AttachmentsAggregation => await QueryAggregationAsync(q, actor, "attachments", ct),
            QueryType.Counters => await QueryCountersAsync(q, actor, ct),
            QueryType.Events => await QueryEventsAsync(q, actor, ct),
            _ => await DispatchTableQuery(q, actor, ct),
        };
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
            return Response.Fail("bad_query",
                $"spaces query requires space_name=\"{managementSpace}\" and subpath=\"/\"");

        var all = await spaces.ListAsync(ct);
        var visible = new List<Space>(all.Count);
        foreach (var space in all)
        {
            // A user can see a space if they have ANY permission referencing it —
            // not just resource_type=space. Most permissions list content/folder/etc.
            // but not "space", yet the user should still see the space in the list.
            if (await perms.HasAnyAccessToSpaceAsync(actor, space.Shortname, ct))
                visible.Add(space);
        }

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
            return Response.Fail("forbidden", "no read access for subpath");

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
            return Response.Fail("forbidden", "no read access for subpath");

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
            return Response.Fail("forbidden", "no read access for subpath");

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
        // Python skips subpath-level permission checks for attachment queries
        // (same as row-level ACL — see QueryHelper.AppendAclFilter).

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
            return Response.Fail("unauthorized", "history queries require authentication");

        if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
            return Response.Fail("forbidden", "no read access for subpath");

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
            return Response.Fail("forbidden", "no read access for subpath");

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
            return Response.Fail("forbidden", "no read access for subpath");

        var pageTask = entries.QueryAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1)
            : entries.CountQueryAsync(q, ct);
        await Task.WhenAll(pageTask, totalTask);

        var records = (await pageTask)
            .Select(e => EntryMapper.ToRecord(e, q.SpaceName, q.RetrieveJsonPayload))
            .ToList();

        // If retrieve_attachments, fetch and attach for each record.
        if (q.RetrieveAttachments && records.Count > 0)
        {
            // Limit concurrency to avoid exhausting the DB connection pool
            var semaphore = new SemaphoreSlim(5);
            var tasks = records.Select(async (rec, _) =>
            {
                await semaphore.WaitAsync(ct);
                try { return await attachments.ListForParentAsync(q.SpaceName, rec.Subpath, rec.Shortname, ct); }
                finally { semaphore.Release(); }
            }).ToArray();
            var allAttachments = await Task.WhenAll(tasks);
            for (var i = 0; i < records.Count; i++)
            {
                if (allAttachments[i].Count > 0)
                {
                    records[i] = records[i] with
                    {
                        Attachments = allAttachments[i]
                            .GroupBy(a => DataAdapters.Sql.JsonbHelpers.EnumMember(a.ResourceType))
                            .ToDictionary(
                                g => g.Key,
                                g => g.Select(a => AttachmentMapper.ToEntryRecord(a)).ToList())
                    };
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
        // Python skips permission checks for attachment queries (see AppendAclFilter).
        if (tableName != "attachments" && !await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
            return Response.Fail("forbidden", "no read access for subpath");

        if (q.AggregationData is null)
            return Response.Fail("bad_query", "aggregation_data required for aggregation queries");

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
                    return Response.Fail("forbidden", "no read access for subpath");
                total = await users.CountQueryAsync(q, ct);
            }
            else if (sub == "roles" || sub.StartsWith("roles/", StringComparison.Ordinal))
            {
                if (!await CanQueryAsync(actor, ResourceType.Role, q.SpaceName, q.Subpath ?? "/", ct))
                    return Response.Fail("forbidden", "no read access for subpath");
                total = await access.CountRolesQueryAsync(q, ct);
            }
            else if (sub == "permissions" || sub.StartsWith("permissions/", StringComparison.Ordinal))
            {
                if (!await CanQueryAsync(actor, ResourceType.Permission, q.SpaceName, q.Subpath ?? "/", ct))
                    return Response.Fail("forbidden", "no read access for subpath");
                total = await access.CountPermissionsQueryAsync(q, ct);
            }
            else
            {
                if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
                    return Response.Fail("forbidden", "no read access for subpath");
                total = await entries.CountQueryAsync(q, ct);
            }
        }
        else
        {
            if (!await CanQueryAsync(actor, ResourceType.Content, q.SpaceName, q.Subpath ?? "/", ct))
                return Response.Fail("forbidden", "no read access for subpath");
            total = await entries.CountQueryAsync(q, ct);
        }

        var returned = Math.Min(Math.Max(total - Math.Max(0, q.Offset), 0), Math.Max(1, q.Limit));
        return Response.Ok(Array.Empty<Record>(), new() { ["total"] = total, ["returned"] = returned });
    }

    // ====================================================================
    // EVENTS (JSONL file reader)
    // ====================================================================
    // Python's events_query() reads {spaces_folder}/{space}/.dm/events.jsonl
    // line-by-line and filters by date range + search. It does NOT use SQL.

    private async Task<Response> QueryEventsAsync(Query q, string? actor, CancellationToken ct)
    {
        if (actor is null)
            return Response.Fail("unauthorized", "events queries require authentication");

        var spacesRoot = settings.Value.SpacesRoot;
        var eventsFile = Path.Combine(spacesRoot, q.SpaceName, ".dm", "events.jsonl");

        if (!File.Exists(eventsFile))
            return Response.Ok(Array.Empty<Record>(), new() { ["total"] = 0, ["returned"] = 0 });

        var records = new List<Record>();
        await foreach (var line in File.ReadLinesAsync(eventsFile, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Date range filtering
                if (q.FromDate is not null && root.TryGetProperty("timestamp", out var ts))
                {
                    if (DateTime.TryParse(ts.GetString(), out var dt) && dt < q.FromDate.Value)
                        continue;
                }
                if (q.ToDate is not null && root.TryGetProperty("timestamp", out var te))
                {
                    if (DateTime.TryParse(te.GetString(), out var dt) && dt > q.ToDate.Value)
                        continue;
                }

                // Search substring filter
                if (!string.IsNullOrEmpty(q.Search) && !line.Contains(q.Search, StringComparison.OrdinalIgnoreCase))
                    continue;

                var attrs = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var prop in root.EnumerateObject())
                    attrs[prop.Name] = prop.Value.Clone();

                records.Add(new Record
                {
                    ResourceType = ResourceType.History,
                    Shortname = root.TryGetProperty("shortname", out var sn) ? sn.GetString() ?? "event" : "event",
                    Subpath = q.Subpath ?? "/",
                    Attributes = attrs,
                });
            }
            catch { /* skip malformed lines */ }
        }

        // Apply offset/limit after filtering
        var total = records.Count;
        var page = records.Skip(Math.Max(0, q.Offset)).Take(Math.Max(1, q.Limit)).ToList();
        return Response.Ok(page, new() { ["total"] = total, ["returned"] = page.Count });
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
            ["acl"] = e.Acl,
            ["payload"] = includePayloadBody ? e.Payload : StripPayloadBody(e.Payload),
            ["relationships"] = e.Relationships,
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
            ["acl"] = s.Acl,
            ["payload"] = s.Payload,
            ["relationships"] = s.Relationships,
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
        // Python strips request_headers and masks passwords in diff.
        var diff = h.Diff;
        if (diff is not null && diff.Contains("password", StringComparison.OrdinalIgnoreCase))
            diff = System.Text.RegularExpressions.Regex.Replace(
                diff, @"""password""\s*:\s*""[^""]*""", @"""password"":""********""");

        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner_shortname"] = h.OwnerShortname,
            ["timestamp"] = h.Timestamp,
            // Deserialize diff JSON string into a JsonElement so it round-trips
            // through the source-gen context without triggering AOT warnings.
            ["diff"] = diff is not null ? JsonDocument.Parse(diff).RootElement.Clone() : null,
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
}
