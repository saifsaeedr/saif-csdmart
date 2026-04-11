using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

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
    // subpath == "/". Everything else falls through to the entries path — we
    // match that so clients get the same error behavior as dmart Python.
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
        return Response.Ok(records, new() { ["total"] = total });
    }

    private async Task<Response> QueryEntriesAsync(Query q, string? actor, CancellationToken ct)
    {
        // Permission gate at subpath level (cheap, single check; result-level
        // filtering is handled below).
        var probe = new Locator(ResourceType.Content, q.SpaceName, q.Subpath ?? "/", "*");
        if (!await perms.CanReadAsync(actor, probe, ct))
            return Response.Fail("forbidden", "no read access for subpath");

        var hits = await entries.QueryAsync(q, ct);
        var records = hits.Select(EntryMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = records.Count });
    }
}

internal static class EntryMapper
{
    public static Record ToRecord(Entry e) => new()
    {
        ResourceType = e.ResourceType,
        Subpath = e.Subpath,
        Shortname = e.Shortname,
        Uuid = e.Uuid,
        Attributes = new()
        {
            ["is_active"] = e.IsActive,
            ["displayname"] = e.Displayname ?? (object)"",
            ["tags"] = e.Tags ?? (object)Array.Empty<string>(),
            ["payload"] = e.Payload ?? (object)new Dictionary<string, object>(),
        },
    };
}

// Projects a Space row into a Record for the /managed/query response. Mirrors
// the field set Python's SQLModel serializer dumps for a Spaces row — the
// Spaces-specific columns (indexing_enabled, languages, hide_space, ...)
// appear alongside the standard Metas base fields in attributes. We drop
// null/empty values to keep the wire form compact, matching Python's
// model_dump(exclude_none=True) behavior.
internal static class SpaceMapper
{
    public static Record ToRecord(Space s)
    {
        var attrs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["is_active"] = s.IsActive,
            ["tags"] = s.Tags,
            ["created_at"] = s.CreatedAt,
            ["updated_at"] = s.UpdatedAt,
            ["owner_shortname"] = s.OwnerShortname,
            ["indexing_enabled"] = s.IndexingEnabled,
            ["capture_misses"] = s.CaptureMisses,
            ["check_health"] = s.CheckHealth,
            ["languages"] = s.Languages,
            ["query_policies"] = s.QueryPolicies,
        };
        if (!string.IsNullOrEmpty(s.Slug)) attrs["slug"] = s.Slug;
        if (s.Displayname is not null) attrs["displayname"] = s.Displayname;
        if (s.Description is not null) attrs["description"] = s.Description;
        if (!string.IsNullOrEmpty(s.OwnerGroupShortname)) attrs["owner_group_shortname"] = s.OwnerGroupShortname;
        if (s.Acl is not null) attrs["acl"] = s.Acl;
        if (s.Payload is not null) attrs["payload"] = s.Payload;
        if (s.Relationships is not null) attrs["relationships"] = s.Relationships;
        if (!string.IsNullOrEmpty(s.LastChecksumHistory)) attrs["last_checksum_history"] = s.LastChecksumHistory;
        if (!string.IsNullOrEmpty(s.RootRegistrationSignature)) attrs["root_registration_signature"] = s.RootRegistrationSignature;
        if (!string.IsNullOrEmpty(s.PrimaryWebsite)) attrs["primary_website"] = s.PrimaryWebsite;
        if (!string.IsNullOrEmpty(s.Icon)) attrs["icon"] = s.Icon;
        if (s.Mirrors is not null) attrs["mirrors"] = s.Mirrors;
        if (s.HideFolders is not null) attrs["hide_folders"] = s.HideFolders;
        if (s.HideSpace is not null) attrs["hide_space"] = s.HideSpace.Value;
        if (s.ActivePlugins is not null) attrs["active_plugins"] = s.ActivePlugins;
        if (s.Ordinal is not null) attrs["ordinal"] = s.Ordinal.Value;

        return new Record
        {
            ResourceType = ResourceType.Space,
            Subpath = s.Subpath,
            Shortname = s.Shortname,
            Uuid = s.Uuid,
            Attributes = attrs,
        };
    }
}
