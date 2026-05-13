using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end coverage of the `move` request type after it grew teeth:
//   - regenerates entries.query_policies so the moved row stays visible to
//     callers whose permissions match the NEW location (and disappears from
//     callers scoped only to the old location);
//   - relocates every attachment anchored at the moved parent so
//     /managed/entry?retrieve_attachments=true still finds them;
//   - cascades through descendant entries (and their attachments) when the
//     moved entry is a Folder;
//   - writes a history row with the `{field: {old, new}}` diff shape so
//     /managed/query?type=history stays uniform.
//
// Tests drive the data layer directly (EntryService.MoveAsync) rather than
// HTTP so the assertions can pin DB invariants without HTTP/permission
// gating noise. ComprehensivePermissionsTests already covers the
// permission-to-move gate.
public sealed class MoveEntryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public MoveEntryTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Move_NonFolder_RegeneratesQueryPolicies()
    {
        var (entries, _, _, svc, _) = Resolve();
        var space = "test";
        var owner = await EnsureOwnerAsync();
        var shortname = Unique("c_qp");
        var oldSubpath = "/move_qp_old";
        var newSubpath = "/move_qp_new";

        var entry = BuildContentEntry(space, oldSubpath, shortname, owner);
        await entries.UpsertAsync(entry);
        try
        {
            var from = new Locator(ResourceType.Content, space, oldSubpath, shortname);
            var to = new Locator(ResourceType.Content, space, newSubpath, shortname);
            var result = await svc.MoveAsync(from, to, owner);
            result.IsOk.ShouldBeTrue($"move should succeed: {result.ErrorMessage}");

            var policies = await GetQueryPoliciesAsync(space, newSubpath, shortname);
            policies.ShouldNotBeEmpty();
            policies.Any(p => p.Contains($":{newSubpath.TrimStart('/')}:content:", StringComparison.Ordinal))
                .ShouldBeTrue($"new-location pattern missing from query_policies: [{string.Join(", ", policies)}]");
            policies.Any(p => p.Contains($":{oldSubpath.TrimStart('/')}:content:", StringComparison.Ordinal))
                .ShouldBeFalse($"old-location pattern must be evicted after move: [{string.Join(", ", policies)}]");
        }
        finally
        {
            await CleanupEntryAsync(space, newSubpath, shortname);
            await CleanupEntryAsync(space, oldSubpath, shortname);
        }
    }

    [FactIfPg]
    public async Task Move_NonFolder_RelocatesAttachments()
    {
        var (entries, attachments, _, svc, _) = Resolve();
        var space = "test";
        var owner = await EnsureOwnerAsync();
        var shortname = Unique("p_att");
        var oldSubpath = "/move_att_old";
        var newSubpath = "/move_att_new";

        var parent = BuildContentEntry(space, oldSubpath, shortname, owner);
        await entries.UpsertAsync(parent);
        var comment = BuildAttachment(space, $"{oldSubpath}/{shortname}", "c1", ResourceType.Comment, owner);
        var media = BuildAttachment(space, $"{oldSubpath}/{shortname}", "m1", ResourceType.Media, owner);
        await attachments.UpsertAsync(comment);
        await attachments.UpsertAsync(media);

        try
        {
            var from = new Locator(ResourceType.Content, space, oldSubpath, shortname);
            var to = new Locator(ResourceType.Content, space, newSubpath, shortname);
            var result = await svc.MoveAsync(from, to, owner);
            result.IsOk.ShouldBeTrue();

            var atNew = await attachments.ListForParentAsync(space, newSubpath, shortname);
            atNew.Select(a => a.Shortname).OrderBy(s => s).ShouldBe(new[] { "c1", "m1" });

            var atOld = await attachments.ListForParentAsync(space, oldSubpath, shortname);
            atOld.ShouldBeEmpty("attachments must not remain at the old anchor");
        }
        finally
        {
            await CleanupAttachmentsForParentAsync(space, $"{newSubpath}/{shortname}");
            await CleanupAttachmentsForParentAsync(space, $"{oldSubpath}/{shortname}");
            await CleanupEntryAsync(space, newSubpath, shortname);
            await CleanupEntryAsync(space, oldSubpath, shortname);
        }
    }

    [FactIfPg]
    public async Task Move_CrossSpace_MovesAttachmentsToDestSpace()
    {
        var (entries, attachments, _, svc, _) = Resolve();
        var srcSpace = "test";
        var destSpace = "test_dest_" + Guid.NewGuid().ToString("N")[..8];
        await EnsureSpaceAsync(destSpace);
        var owner = await EnsureOwnerAsync();
        var shortname = Unique("p_xs");
        var subpath = "/move_xs";

        var parent = BuildContentEntry(srcSpace, subpath, shortname, owner);
        await entries.UpsertAsync(parent);
        var att = BuildAttachment(srcSpace, $"{subpath}/{shortname}", "c1", ResourceType.Comment, owner);
        await attachments.UpsertAsync(att);

        try
        {
            var from = new Locator(ResourceType.Content, srcSpace, subpath, shortname);
            var to = new Locator(ResourceType.Content, destSpace, subpath, shortname);
            var result = await svc.MoveAsync(from, to, owner);
            result.IsOk.ShouldBeTrue($"cross-space move should succeed: {result.ErrorMessage}");

            var atDest = await attachments.ListForParentAsync(destSpace, subpath, shortname);
            atDest.Count.ShouldBe(1);
            atDest[0].SpaceName.ShouldBe(destSpace, "attachment must follow entry into the destination space");

            var atSrc = await attachments.ListForParentAsync(srcSpace, subpath, shortname);
            atSrc.ShouldBeEmpty();

            // query_policies encode space in every pattern. A regression where
            // cross-space moves regen against the source space would silently
            // make the moved entry invisible to callers scoped to destSpace.
            var policies = await GetQueryPoliciesAsync(destSpace, subpath, shortname);
            policies.ShouldNotBeEmpty();
            policies.Any(p => p.StartsWith($"{destSpace}:", StringComparison.Ordinal))
                .ShouldBeTrue($"cross-space policies must anchor to destSpace: [{string.Join(", ", policies)}]");
            policies.Any(p => p.StartsWith($"{srcSpace}:", StringComparison.Ordinal))
                .ShouldBeFalse($"old-space pattern must be evicted after cross-space move: [{string.Join(", ", policies)}]");
        }
        finally
        {
            await CleanupAttachmentsForParentAsync(destSpace, $"{subpath}/{shortname}");
            await CleanupAttachmentsForParentAsync(srcSpace, $"{subpath}/{shortname}");
            await CleanupEntryAsync(destSpace, subpath, shortname);
            await CleanupEntryAsync(srcSpace, subpath, shortname);
            await CleanupSpaceAsync(destSpace);
        }
    }

    [FactIfPg]
    public async Task Move_Folder_CascadesDescendantsAndTheirAttachments()
    {
        var (entries, attachments, _, svc, _) = Resolve();
        var space = "test";
        var owner = await EnsureOwnerAsync();
        var folderName = Unique("fld");
        var renamedFolder = Unique("fld_r");

        // Folder at /; child entry at /<folder>; grandchild at /<folder>/child.
        var folder = BuildFolder(space, "/", folderName, owner);
        var child = BuildContentEntry(space, $"/{folderName}", "child", owner);
        var grandchild = BuildContentEntry(space, $"/{folderName}/child", "grandchild", owner);
        await entries.UpsertAsync(folder);
        await entries.UpsertAsync(child);
        await entries.UpsertAsync(grandchild);

        var folderAtt = BuildAttachment(space, $"/{folderName}", "fa1", ResourceType.Comment, owner);
        var childAtt = BuildAttachment(space, $"/{folderName}/child", "ca1", ResourceType.Comment, owner);
        var grandchildAtt = BuildAttachment(space, $"/{folderName}/child/grandchild", "ga1", ResourceType.Comment, owner);
        await attachments.UpsertAsync(folderAtt);
        await attachments.UpsertAsync(childAtt);
        await attachments.UpsertAsync(grandchildAtt);

        try
        {
            var from = new Locator(ResourceType.Folder, space, "/", folderName);
            var to = new Locator(ResourceType.Folder, space, "/", renamedFolder);
            var result = await svc.MoveAsync(from, to, owner);
            result.IsOk.ShouldBeTrue($"folder move should succeed: {result.ErrorMessage}");

            (await entries.GetAsync(space, "/", renamedFolder, ResourceType.Folder))
                .ShouldNotBeNull("folder must exist at new location");
            (await entries.GetAsync(space, $"/{renamedFolder}", "child", ResourceType.Content))
                .ShouldNotBeNull("child must follow folder cascade");
            (await entries.GetAsync(space, $"/{renamedFolder}/child", "grandchild", ResourceType.Content))
                .ShouldNotBeNull("grandchild must follow folder cascade");

            (await entries.GetAsync(space, "/", folderName, ResourceType.Folder))
                .ShouldBeNull("folder row must be gone from the old location");
            (await entries.GetAsync(space, $"/{folderName}", "child", ResourceType.Content))
                .ShouldBeNull("child row must be gone from the old subtree");

            (await attachments.ListForParentAsync(space, "/", renamedFolder)).Count.ShouldBe(1);
            (await attachments.ListForParentAsync(space, $"/{renamedFolder}", "child")).Count.ShouldBe(1);
            (await attachments.ListForParentAsync(space, $"/{renamedFolder}/child", "grandchild")).Count.ShouldBe(1);

            (await attachments.ListForParentAsync(space, "/", folderName)).ShouldBeEmpty();
            (await attachments.ListForParentAsync(space, $"/{folderName}", "child")).ShouldBeEmpty();
        }
        finally
        {
            await CleanupAttachmentsForParentAsync(space, $"/{renamedFolder}");
            await CleanupAttachmentsForParentAsync(space, $"/{renamedFolder}/child");
            await CleanupAttachmentsForParentAsync(space, $"/{renamedFolder}/child/grandchild");
            await CleanupAttachmentsForParentAsync(space, $"/{folderName}");
            await CleanupAttachmentsForParentAsync(space, $"/{folderName}/child");
            await CleanupAttachmentsForParentAsync(space, $"/{folderName}/child/grandchild");
            await CleanupEntryAsync(space, $"/{renamedFolder}/child", "grandchild");
            await CleanupEntryAsync(space, $"/{renamedFolder}", "child");
            await CleanupEntryAsync(space, "/", renamedFolder);
            await CleanupEntryAsync(space, $"/{folderName}/child", "grandchild");
            await CleanupEntryAsync(space, $"/{folderName}", "child");
            await CleanupEntryAsync(space, "/", folderName);
        }
    }

    [FactIfPg]
    public async Task Move_Folder_RegeneratesDescendantQueryPolicies()
    {
        var (entries, _, _, svc, _) = Resolve();
        var space = "test";
        var owner = await EnsureOwnerAsync();
        var folderName = Unique("fld_qp");
        var renamedFolder = Unique("fld_qpr");

        await entries.UpsertAsync(BuildFolder(space, "/", folderName, owner));
        await entries.UpsertAsync(BuildContentEntry(space, $"/{folderName}", "child", owner));

        try
        {
            var from = new Locator(ResourceType.Folder, space, "/", folderName);
            var to = new Locator(ResourceType.Folder, space, "/", renamedFolder);
            (await svc.MoveAsync(from, to, owner)).IsOk.ShouldBeTrue();

            var folderPolicies = await GetQueryPoliciesAsync(space, "/", renamedFolder);
            folderPolicies.Any(p => p.Contains($":{renamedFolder}:folder:", StringComparison.Ordinal))
                .ShouldBeTrue($"folder policies still anchored to old name: [{string.Join(", ", folderPolicies)}]");

            var childPolicies = await GetQueryPoliciesAsync(space, $"/{renamedFolder}", "child");
            childPolicies.Any(p => p.Contains($":{renamedFolder}:content:", StringComparison.Ordinal))
                .ShouldBeTrue($"descendant policies still anchored to old folder name: [{string.Join(", ", childPolicies)}]");
            childPolicies.Any(p => p.Contains($":{folderName}:content:", StringComparison.Ordinal))
                .ShouldBeFalse($"old folder-name pattern must be evicted from descendant after cascade: [{string.Join(", ", childPolicies)}]");
        }
        finally
        {
            await CleanupEntryAsync(space, $"/{renamedFolder}", "child");
            await CleanupEntryAsync(space, "/", renamedFolder);
            await CleanupEntryAsync(space, $"/{folderName}", "child");
            await CleanupEntryAsync(space, "/", folderName);
        }
    }

    [FactIfPg]
    public async Task Move_NewLocationGrantee_CanSeeMovedEntry()
    {
        var (_, _, _, svc, _) = Resolve();
        var perms = _factory.Services.GetRequiredService<PermissionService>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var space = "test";
        var owner = await EnsureOwnerAsync();
        var oldSubpath = "/private";
        var newSubpath = "/shared";
        var shortname = Unique("vis");

        var permName = Unique("perm_vis");
        var roleName = Unique("role_vis");
        var userName = Unique("user_vis");

        var entry = BuildContentEntry(space, oldSubpath, shortname, owner);
        await entries.UpsertAsync(entry);

        try
        {
            await access.UpsertPermissionAsync(new Permission
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = permName,
                SpaceName = "management",
                Subpath = "/permissions",
                OwnerShortname = "dmart",
                IsActive = true,
                Subpaths = new() { [space] = new() { newSubpath.TrimStart('/') } },
                Actions = new() { "view", "query" },
                ResourceTypes = new() { "content" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await access.UpsertRoleAsync(new Role
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = roleName,
                SpaceName = "management",
                Subpath = "/roles",
                OwnerShortname = "dmart",
                IsActive = true,
                Permissions = new() { permName },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = userName,
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = userName,
                IsActive = true,
                Type = UserType.Web,
                Language = Language.En,
                Roles = new() { roleName },
                Groups = new(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await access.InvalidateAllCachesAsync();

            // Before move: user has view permission only on /shared, so the
            // entry at /private is denied.
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, space, oldSubpath, shortname)))
                .ShouldBeFalse("user without permission on /private cannot see the entry pre-move");

            (await svc.MoveAsync(
                new Locator(ResourceType.Content, space, oldSubpath, shortname),
                new Locator(ResourceType.Content, space, newSubpath, shortname),
                owner)).IsOk.ShouldBeTrue();

            // After move: same user gains permission via /shared scope.
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, space, newSubpath, shortname)))
                .ShouldBeTrue("user with permission on /shared must see the moved entry");
        }
        finally
        {
            await CleanupEntryAsync(space, newSubpath, shortname);
            await CleanupEntryAsync(space, oldSubpath, shortname);
            try { await users.DeleteAsync(userName); } catch { }
            try { await access.DeleteRoleAsync(roleName); } catch { }
            try { await access.DeletePermissionAsync(permName); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task Move_WritesHistoryRow()
    {
        var (entries, _, _, svc, _) = Resolve();
        var space = "test";
        var owner = await EnsureOwnerAsync();
        var shortname = Unique("h");
        var oldSubpath = "/hist_old";
        var newSubpath = "/hist_new";

        await entries.UpsertAsync(BuildContentEntry(space, oldSubpath, shortname, owner));
        try
        {
            (await svc.MoveAsync(
                new Locator(ResourceType.Content, space, oldSubpath, shortname),
                new Locator(ResourceType.Content, space, newSubpath, shortname),
                owner)).IsOk.ShouldBeTrue();

            var diff = await GetLatestHistoryDiffAsync(space, newSubpath, shortname);
            diff.ShouldNotBeNull("move must write a history row at the destination");
            diff!.ShouldContain("\"space_name\"");
            diff.ShouldContain("\"subpath\"");
            diff.ShouldContain("\"shortname\"");
            diff.ShouldContain("\"old\"");
            diff.ShouldContain("\"new\"");
            diff.ShouldContain(oldSubpath);
            diff.ShouldContain(newSubpath);
        }
        finally
        {
            await CleanupHistoryAsync(space, newSubpath, shortname);
            await CleanupEntryAsync(space, newSubpath, shortname);
            await CleanupEntryAsync(space, oldSubpath, shortname);
        }
    }

    [FactIfPg]
    public async Task Move_IsAtomic_OnAttachmentConflict()
    {
        var (entries, attachments, _, svc, _) = Resolve();
        var space = "test";
        var owner = await EnsureOwnerAsync();
        var shortname = Unique("atom");
        var oldSubpath = "/atom_old";
        var newSubpath = "/atom_new";

        // Source: entry + one attachment.
        await entries.UpsertAsync(BuildContentEntry(space, oldSubpath, shortname, owner));
        var srcAtt = BuildAttachment(space, $"{oldSubpath}/{shortname}", "c1", ResourceType.Comment, owner);
        await attachments.UpsertAsync(srcAtt);

        // Pre-create an attachment at the DESTINATION anchor with the same
        // shortname — moving the source's attachment would violate
        // attachments' UNIQUE (shortname, space_name, subpath). The move must
        // either roll back fully or skip the conflict; this test pins the
        // "fully roll back" branch so partial moves never ship to prod.
        var blocker = BuildAttachment(space, $"{newSubpath}/{shortname}", "c1", ResourceType.Comment, owner);
        await attachments.UpsertAsync(blocker);

        try
        {
            // The current implementation uses a positional-prefix UPDATE that
            // does NOT skip conflicting rows. The repository surfaces the
            // 23505 from Postgres; the service layer maps it to
            // SHORTNAME_ALREADY_EXIST so the HTTP layer can return a real
            // 409-shaped error instead of a 500. The DB transaction still
            // rolls back fully — partial moves never ship to prod.
            var result = await svc.MoveAsync(
                new Locator(ResourceType.Content, space, oldSubpath, shortname),
                new Locator(ResourceType.Content, space, newSubpath, shortname),
                owner);
            result.IsOk.ShouldBeFalse("destination conflict must fail the move");
            result.ErrorCode.ShouldBe(InternalErrorCode.SHORTNAME_ALREADY_EXIST,
                $"expected SHORTNAME_ALREADY_EXIST, got code={result.ErrorCode}, message={result.ErrorMessage}");

            // Source entry still at the old location.
            (await entries.GetAsync(space, oldSubpath, shortname, ResourceType.Content))
                .ShouldNotBeNull("transaction must roll back — source entry stays put");

            // Source attachment still at the old anchor.
            var atOld = await attachments.ListForParentAsync(space, oldSubpath, shortname);
            atOld.Any(a => a.Shortname == "c1")
                .ShouldBeTrue("source attachment must remain at old anchor after rollback");
        }
        finally
        {
            await CleanupAttachmentsForParentAsync(space, $"{newSubpath}/{shortname}");
            await CleanupAttachmentsForParentAsync(space, $"{oldSubpath}/{shortname}");
            await CleanupEntryAsync(space, newSubpath, shortname);
            await CleanupEntryAsync(space, oldSubpath, shortname);
        }
    }

    // ============================================================
    // Helpers
    // ============================================================

    private (EntryRepository entries, AttachmentRepository attachments,
             HistoryRepository history, EntryService service, Db db) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<AttachmentRepository>(),
            sp.GetRequiredService<HistoryRepository>(),
            sp.GetRequiredService<EntryService>(),
            sp.GetRequiredService<Db>());
    }

    // Cap at 24 chars to keep the test-row shortnames readable while keeping
    // enough GUID-hex tail (≥10 chars after the longest prefix used in this
    // file) that parallel xunit collisions are vanishingly unlikely.
    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    // The move service does CanUpdateAsync(actor, src) + CanCreateAsync(actor, dst)
    // before mutating. Run the suite as admin-level so those gates never fail —
    // permission-to-move is already covered by ComprehensivePermissionsTests.
    private async Task<string> EnsureOwnerAsync()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var actor = $"itest_mv_{Guid.NewGuid():N}"[..16];
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = actor,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = actor,
            IsActive = true,
            Type = UserType.Web,
            Language = Language.En,
            Roles = new() { "super_admin" },
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _factory.Services.GetRequiredService<AccessRepository>().InvalidateAllCachesAsync();
        return actor;
    }

    private async Task EnsureSpaceAsync(string spaceName)
    {
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task CleanupSpaceAsync(string spaceName)
    {
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        try { await spaces.DeleteAsync(spaceName); } catch { }
    }

    private static Entry BuildContentEntry(string space, string subpath, string shortname, string owner)
        => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = owner,
            ResourceType = ResourceType.Content,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static Entry BuildFolder(string space, string subpath, string shortname, string owner)
        => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = owner,
            ResourceType = ResourceType.Folder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static Attachment BuildAttachment(
        string space, string subpath, string shortname, ResourceType type, string owner)
        => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = owner,
            ResourceType = type,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private async Task<List<string>> GetQueryPoliciesAsync(string spaceName, string subpath, string shortname)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT query_policies FROM entries WHERE space_name = $1 AND subpath = $2 AND shortname = $3",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = Locator.NormalizeSubpath(subpath) });
        cmd.Parameters.Add(new() { Value = shortname });
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return new();
        return ((string[])r.GetValue(0)).ToList();
    }

    private async Task<string?> GetLatestHistoryDiffAsync(string spaceName, string subpath, string shortname)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT diff::text FROM histories WHERE space_name = $1 AND subpath = $2 AND shortname = $3 ORDER BY timestamp DESC LIMIT 1",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = Locator.NormalizeSubpath(subpath) });
        cmd.Parameters.Add(new() { Value = shortname });
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    private async Task CleanupEntryAsync(string spaceName, string subpath, string shortname)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM entries WHERE space_name = $1 AND subpath = $2 AND shortname = $3",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = Locator.NormalizeSubpath(subpath) });
        cmd.Parameters.Add(new() { Value = shortname });
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    private async Task CleanupAttachmentsForParentAsync(string spaceName, string parentPath)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM attachments WHERE space_name = $1 AND (subpath = $2 OR subpath LIKE $2 || '/%')",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = Locator.NormalizeSubpath(parentPath) });
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    private async Task CleanupHistoryAsync(string spaceName, string subpath, string shortname)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM histories WHERE space_name = $1 AND subpath = $2 AND shortname = $3",
            conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = Locator.NormalizeSubpath(subpath) });
        cmd.Parameters.Add(new() { Value = shortname });
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }
}
