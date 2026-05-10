using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end coverage for `WorkflowService.ProgressAsync` — specifically the
// permission gate added in PR #17. The call now threads `actionOverride:
// "progress_ticket"` into `EntryService.UpdateAsync`, so:
//   - a user with ONLY `progress_ticket` permission can progress a ticket
//     (previously denied because the gate was on `update`)
//   - a user with ONLY `update` permission CANNOT progress a ticket
//     (previously allowed by accident)
//
// `WorkflowService.ProgressAsync` is otherwise uncovered — `Dmart.Services.
// WorkflowService.ProgressAsync` line-rate is 0 in the cobertura baseline.
// These tests pin both directions of the gate so a regression on either side
// surfaces immediately.
public sealed class WorkflowServiceProgressTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;

    public WorkflowServiceProgressTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Progress_Succeeds_For_User_With_Only_ProgressTicket_Permission()
    {
        var ctx = await SetupAsync(grantedActions: new() { "progress_ticket" });
        try
        {
            // Initial state is "draft"; workflow allows draft → submitted via
            // the "submit" action. The user has progress_ticket but NOT update,
            // so this only succeeds if the new gate is in place.
            var resp = await ctx.Workflow.ProgressAsync(
                ctx.TicketLocator, action: "submit", actor: ctx.UserName, attrs: null);

            resp.Status.ShouldBe(Status.Success,
                $"progress_ticket permission must allow ProgressAsync; got error {resp.Error?.Code}/{resp.Error?.Message}");
            // Sanity: the response carries the new state so callers can react
            // without re-querying.
            resp.Attributes.ShouldNotBeNull();
            resp.Attributes!["state"]?.ToString().ShouldBe("submitted");
        }
        finally { await ctx.CleanupAsync(); }
    }

    [FactIfPg]
    public async Task Progress_Denies_User_With_Only_Update_Permission()
    {
        var ctx = await SetupAsync(grantedActions: new() { "update" });
        try
        {
            // The user has plain `update` permission on the ticket but NOT
            // `progress_ticket`. With the new gate, the WorkflowService must
            // reject this — even though the patch payload (state, is_open)
            // looks like a regular update at the storage layer.
            var resp = await ctx.Workflow.ProgressAsync(
                ctx.TicketLocator, action: "submit", actor: ctx.UserName, attrs: null);

            resp.Status.ShouldBe(Status.Failed);
            resp.Error.ShouldNotBeNull();
            resp.Error!.Code.ShouldBe((int)InternalErrorCode.NOT_ALLOWED,
                "an actor without progress_ticket must be denied at the action gate, "
                + "regardless of any update permission they hold");
        }
        finally { await ctx.CleanupAsync(); }
    }

    // ------------------------------------------------------------------
    // Setup helpers
    // ------------------------------------------------------------------

    // Creates a one-off space, a workflow definition with a draft → submitted
    // → approved state machine, a ticket in state "draft", and a user whose
    // role grants exactly one action against the ticket subpath. Returns a
    // context object so the [FactIfPg] body stays focused on the assertion.
    private async Task<TestContext> SetupAsync(List<string> grantedActions)
    {
        _factory.CreateClient(); // boots AdminBootstrap → ensures `dmart` user exists
        var sp = _factory.Services;
        var ctx = new TestContext
        {
            Services = sp,
            Workflow = sp.GetRequiredService<WorkflowService>(),
            Entries = sp.GetRequiredService<EntryRepository>(),
            Spaces = sp.GetRequiredService<SpaceRepository>(),
            Users = sp.GetRequiredService<UserRepository>(),
            Access = sp.GetRequiredService<AccessRepository>(),
            // Truncated to 32 chars to fit the shortname column constraint
            // (matches the convention in ComprehensivePermissionsTests).
            SpaceName = Unique("wfprog_sp"),
            WorkflowName = Unique("wfprog_wf"),
            TicketName = Unique("wfprog_tk"),
            PermName = Unique("wfprog_perm"),
            RoleName = Unique("wfprog_role"),
            UserName = Unique("wfprog_user"),
        };
        ctx.TicketLocator = new Locator(
            ResourceType.Ticket, ctx.SpaceName, "/tickets", ctx.TicketName);

        var now = DateTime.UtcNow;

        // 1. Space — required so the FK from entries.space_name resolves.
        await ctx.Spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ctx.SpaceName,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            ResourceType = ResourceType.Space,
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });

        // 2. Workflow definition — Content entry under /workflows. Engine
        //    looks here first when LoadWorkflowAsync resolves a ticket's
        //    workflow_shortname (WorkflowEngine.cs:146).
        var workflowJson = JsonDocument.Parse("""
            {
              "initial_state": "draft",
              "states": [
                {"state": "draft", "next": [{"action": "submit", "to": "submitted"}]},
                {"state": "submitted", "next": [{"action": "approve", "to": "approved"}]}
              ],
              "closed_states": ["approved"]
            }
            """);
        await ctx.Entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ctx.WorkflowName,
            SpaceName = ctx.SpaceName,
            Subpath = "/workflows",
            OwnerShortname = "dmart",
            ResourceType = ResourceType.Content,
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = workflowJson.RootElement.Clone(),
            },
            CreatedAt = now,
            UpdatedAt = now,
        });

        // 3. Ticket in initial state "draft" — must match the workflow's
        //    initial_state or the engine rejects the submit transition with
        //    "transition not allowed" before the gate even fires.
        await ctx.Entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ctx.TicketName,
            SpaceName = ctx.SpaceName,
            Subpath = "/tickets",
            OwnerShortname = "dmart",
            ResourceType = ResourceType.Ticket,
            IsActive = true,
            State = "draft",
            IsOpen = true,
            WorkflowShortname = ctx.WorkflowName,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // 4. Permission/role/user with EXACTLY the granted actions. Subpath
        //    set is `tickets` (no leading slash — that's the storage form
        //    the permission system expects, see PublicQueryAnonymousTests).
        await ctx.Access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ctx.PermName,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [ctx.SpaceName] = new() { "tickets" } },
            Actions = grantedActions,
            ResourceTypes = new() { "ticket" },
            Conditions = new(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.Access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ctx.RoleName,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { ctx.PermName },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.Users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = ctx.UserName,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = ctx.UserName,
            IsActive = true,
            Type = UserType.Web,
            Language = Language.En,
            Roles = new() { ctx.RoleName },
            Groups = new(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.Access.InvalidateAllCachesAsync();

        return ctx;
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..32];

    // Test fixture state — bundles every disposable resource so the [FactIfPg]
    // body never has to thread them through finally blocks individually.
    private sealed class TestContext
    {
        public required IServiceProvider Services { get; init; }
        public required WorkflowService Workflow { get; init; }
        public required EntryRepository Entries { get; init; }
        public required SpaceRepository Spaces { get; init; }
        public required UserRepository Users { get; init; }
        public required AccessRepository Access { get; init; }
        public required string SpaceName { get; init; }
        public required string WorkflowName { get; init; }
        public required string TicketName { get; init; }
        public required string PermName { get; init; }
        public required string RoleName { get; init; }
        public required string UserName { get; init; }
        public Locator TicketLocator { get; set; } = null!;

        public async Task CleanupAsync()
        {
            // Order matters: entries first (FK to space), then user/role/perm,
            // then space last. Each step is best-effort because a partial
            // setup in a failed test should still tear down what landed.
            try { await Entries.DeleteAsync(SpaceName, "/tickets", TicketName, ResourceType.Ticket); } catch { }
            try { await Entries.DeleteAsync(SpaceName, "/workflows", WorkflowName, ResourceType.Content); } catch { }
            try { await Users.DeleteAsync(UserName); } catch { }
            try { await Access.DeleteRoleAsync(RoleName); } catch { }
            try { await Access.DeletePermissionAsync(PermName); } catch { }
            try { await Spaces.DeleteAsync(SpaceName); } catch { }
            await Access.InvalidateAllCachesAsync();
        }
    }
}
