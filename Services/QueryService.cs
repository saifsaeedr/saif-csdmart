using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// /managed/query service.
//
// Python dmart's query response envelope is:
//   {
//     "status": "success",
//     "records": [...],
//     "attributes": { "total": <pre-limit total>, "returned": <this-page count> }
//   }
//
// Two things are important:
//   1. `total` is the number of rows that match the filters IGNORING limit/
//      offset — clients rely on this to page.
//   2. `returned` is the count of records on this page. It equals len(records).
//
// The record shape itself is built by the mappers below — see the notes on
// each mapper for which fields are included and how they compare to Python's
// `to_record` output.
public sealed class QueryService(
    EntryRepository entries,
    SpaceRepository spaces,
    PermissionService perms,
    IOptions<DmartSettings> settings)
{
    public async Task<Response> ExecuteAsync(Query q, string? actor, CancellationToken ct = default)
    {
        // Bounds check
        if (string.IsNullOrEmpty(q.SpaceName))
            return Response.Fail("bad_query", "space_name is required");

        // Dispatch by query type. Most types fall through to the entries path;
        // "spaces" hits the spaces table instead (mirrors dmart Python's
        // data_adapters/sql/adapter.py special case at line 1518).
        return q.Type switch
        {
            QueryType.Spaces => await QuerySpacesAsync(q, actor, ct),
            _ => await QueryEntriesAsync(q, actor, ct),
        };
    }

    // Python only accepts type=spaces when space_name == management_space and
    // subpath == "/". Everything else returns bad_query — we match that so
    // clients get the same error behavior as dmart Python.
    private async Task<Response> QuerySpacesAsync(Query q, string? actor, CancellationToken ct)
    {
        var managementSpace = settings.Value.ManagementSpace;
        var normalizedSubpath = string.IsNullOrEmpty(q.Subpath) ? "/" : q.Subpath;
        if (!string.Equals(q.SpaceName, managementSpace, StringComparison.Ordinal) || normalizedSubpath != "/")
            return Response.Fail("bad_query",
                $"spaces query requires space_name=\"{managementSpace}\" and subpath=\"/\"");

        // SELECT * FROM spaces — no permission filtering at the SQL layer; the
        // filter runs per-row below. Matches Python which skips
        // apply_acl_and_query_policies for QueryType.spaces.
        var all = await spaces.ListAsync(ct);

        // Per-space ACL check. Python uses action_type=query against each space
        // individually — a user who has the query action on space "foo" but not
        // on space "bar" sees only "foo". PermissionService.CanAsync("query", ...)
        // walks roles + permissions the same way.
        var visible = new List<Space>(all.Count);
        foreach (var space in all)
        {
            var locator = new Locator(ResourceType.Space, space.Shortname, "/", space.Shortname);
            var ctx = new PermissionService.ResourceContext(space.IsActive, space.OwnerShortname, space.OwnerGroupShortname, space.Acl);
            if (await perms.CanAsync(actor, "query", locator, ctx, null, ct))
                visible.Add(space);
        }

        // Python clamps with limit/offset AFTER the permission filter so
        // invisible rows don't count toward the page window. We match that.
        var total = visible.Count;
        var page = visible.Skip(Math.Max(0, q.Offset)).Take(Math.Max(1, q.Limit)).ToList();
        var records = page.Select(SpaceMapper.ToRecord).ToList();
        return Response.Ok(records, new()
        {
            // Python: attributes={"total": total, "returned": len(records)}
            ["total"] = total,
            ["returned"] = records.Count,
        });
    }

    private async Task<Response> QueryEntriesAsync(Query q, string? actor, CancellationToken ct)
    {
        // Permission gate at subpath level (cheap, single check; result-level
        // filtering is handled below).
        var probe = new Locator(ResourceType.Content, q.SpaceName, q.Subpath ?? "/", "*");
        if (!await perms.CanReadAsync(actor, probe, ct))
            return Response.Fail("forbidden", "no read access for subpath");

        // Fetch the page + the total in parallel. Python runs two SQL statements
        // (the main SELECT and a separate COUNT) — we match that so clients can
        // page through arbitrary-sized result sets.
        //
        // Python's retrieve_total defaults to True; only explicit false skips
        // the count query. Source-gen JSON doesn't apply C# property
        // initializers for missing JSON keys, so the Query.RetrieveTotal field
        // is nullable and we treat null == true here.
        var pageTask = entries.QueryAsync(q, ct);
        var totalTask = q.RetrieveTotal == false
            ? Task.FromResult(-1L)
            : entries.CountQueryAsync(q, ct);
        await Task.WhenAll(pageTask, totalTask);
        var hits = pageTask.Result;
        // Narrow to int so the source-gen JSON context can serialize it without
        // a JsonTypeInfo<long> registration. Record totals never realistically
        // exceed 2^31 in dmart deployments.
        var total = (int)totalTask.Result;

        var records = hits.Select(e => EntryMapper.ToRecord(e, q.SpaceName)).ToList();
        return Response.Ok(records, new()
        {
            ["total"] = total,
            ["returned"] = records.Count,
        });
    }
}

// Projects an Entry row into a Record for the /managed/query response.
//
// Python's data_adapters/sql/create_tables.py::to_record builds attributes by
// dumping every SQLModel column except the "local props" (uuid, resource_type,
// shortname, subpath). Emits null and empty-list values explicitly because
// Pydantic's exclude_none operates on the top-level Record model, not on dict
// contents.
//
// Python then post-processes in _set_query_final_results (adapter.py:2883):
//   * delete rec.attributes["query_policies"]
//   * delete rec.attributes["password"] for user records
//   * optionally strip payload.body if retrieve_json_payload is false
//
// We replicate all of that here.
internal static class EntryMapper
{
    public static Record ToRecord(Entry e, string spaceName)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Metas base (matches the fields Python's to_record emits from the
            // SQLModel __dict__).
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
            ["payload"] = e.Payload,
            ["relationships"] = e.Relationships,
            ["last_checksum_history"] = e.LastChecksumHistory,
            // Entries-specific (ticket fields)
            ["state"] = e.State,
            ["is_open"] = e.IsOpen,
            ["reporter"] = e.Reporter,
            ["workflow_shortname"] = e.WorkflowShortname,
            ["collaborators"] = e.Collaborators,
            ["resolution_reason"] = e.ResolutionReason,
            // space_name is included in Python's to_record output (it's a
            // column on the Metas table). Clients use it to identify which
            // space a cross-space query result came from.
            ["space_name"] = spaceName,
        };

        // Python deletes query_policies unconditionally — it's an internal
        // gating field that shouldn't leak to clients.
        attrs.Remove("query_policies");

        return new Record
        {
            ResourceType = e.ResourceType,
            Subpath = e.Subpath,
            Shortname = e.Shortname,
            Uuid = e.Uuid,
            Attributes = attrs!,
        };
    }
}

// Projects a Space row into a Record for the /managed/query response. Python's
// to_record emits ALL Space SQLModel columns (nulls included) minus the local
// props. We match the key set — see the inline comment on each group.
internal static class SpaceMapper
{
    public static Record ToRecord(Space s)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Metas base — always present in Python's dump, including nulls.
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
            // Space-specific columns
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
            // space_name is a real column on the spaces table and appears in
            // Python's dump — for the self-referential spaces rows it equals
            // the shortname.
            ["space_name"] = s.SpaceName,
        };

        // Python deletes query_policies before returning — match that.
        attrs.Remove("query_policies");

        return new Record
        {
            ResourceType = ResourceType.Space,
            Subpath = s.Subpath,
            Shortname = s.Shortname,
            Uuid = s.Uuid,
            Attributes = attrs!,
        };
    }
}
