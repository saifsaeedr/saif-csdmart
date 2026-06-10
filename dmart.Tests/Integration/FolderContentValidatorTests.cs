using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Direct tests of FolderContentValidator — mirrors UniqueFieldsManagedTests:
// resolve the validator from DI, seed a space + folder carrying the policy body,
// then assert ValidateAsync / ValidateRawAsync outcomes. The validator is
// action-agnostic, so these cover both create and update logic; Task 4/5 prove
// the validator is actually invoked on those paths.
public class FolderContentValidatorTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public FolderContentValidatorTests(DmartFactory factory) => _factory = factory;

    private (SpaceRepository spaces, EntryRepository entries, FolderContentValidator validator) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<SpaceRepository>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<FolderContentValidator>());
    }

    // Seeds a fresh space with one folder at "/" whose payload.body is the given
    // raw JSON object. Returns the space name. Entries created "under" the folder
    // live at subpath "/<folderShortname>".
    private static async Task<string> SeedSpaceWithFolderAsync(
        SpaceRepository spaces, EntryRepository entries, string folderShortname, string bodyJson)
    {
        var spaceName = $"fcv_{Guid.NewGuid():N}"[..16];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = "dmart", IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = folderShortname, SpaceName = spaceName, Subpath = "/",
            ResourceType = ResourceType.Folder, IsActive = true, OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(bodyJson).RootElement.Clone(),
            },
        });
        return spaceName;
    }

    private static Entry MakeEntry(
        string space, string subpath, ResourceType type,
        string? schema = null, string? workflow = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = $"e_{Guid.NewGuid():N}"[..10],
        SpaceName = space, Subpath = subpath, ResourceType = type,
        IsActive = true, OwnerShortname = "dmart",
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        WorkflowShortname = workflow,
        Payload = schema is null ? null : new Payload { ContentType = ContentType.Json, SchemaShortname = schema },
    };

    // ---- content_resource_types ------------------------------------------------

    [FactIfPg]
    public async Task ContentResourceTypes_AllowedType_Passes()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"content_resource_types":["content"]}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Content));
            res.IsOk.ShouldBeTrue($"content under a content-only folder should pass: {res.ErrorMessage}");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    [FactIfPg]
    public async Task ContentResourceTypes_DisallowedType_Rejected()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"content_resource_types":["content"]}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Ticket));
            res.IsOk.ShouldBeFalse("ticket under a content-only folder should be rejected");
            res.ErrorCode.ShouldBe(InternalErrorCode.INVALID_DATA);
            res.ErrorType.ShouldBe(ErrorTypes.Request);
            res.ErrorMessage!.ShouldContain("resource type");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    [FactIfPg]
    public async Task ContentResourceTypes_EmptyList_NoRestriction()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"content_resource_types":[]}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Ticket));
            res.IsOk.ShouldBeTrue("empty list imposes no restriction");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    [FactIfPg]
    public async Task ContentResourceTypes_AbsentKey_NoRestriction()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"icon":"x"}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Ticket));
            res.IsOk.ShouldBeTrue("absent key imposes no restriction");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    // ---- content_schema_shortnames ---------------------------------------------

    [FactIfPg]
    public async Task ContentSchema_AllowedSchema_Passes()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"content_schema_shortnames":["product"]}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Content, schema: "product"));
            res.IsOk.ShouldBeTrue($"allowed schema should pass: {res.ErrorMessage}");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    [FactIfPg]
    public async Task ContentSchema_DisallowedSchema_Rejected()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"content_schema_shortnames":["product"]}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Content, schema: "other"));
            res.IsOk.ShouldBeFalse("disallowed schema should be rejected");
            res.ErrorCode.ShouldBe(InternalErrorCode.INVALID_DATA);
            res.ErrorMessage!.ShouldContain("schema");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    [FactIfPg]
    public async Task ContentSchema_NoSchemaDeclared_Passes()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"content_schema_shortnames":["product"]}""");
        try
        {
            // schema-absent => allow (the gate only fires for entries that declare a schema)
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Content, schema: null));
            res.IsOk.ShouldBeTrue("entry with no schema must not be forced to adopt one");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    // ---- workflow_shortnames ---------------------------------------------------

    [FactIfPg]
    public async Task Workflow_TicketAllowedWorkflow_Passes()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"workflow_shortnames":["approval"]}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Ticket, workflow: "approval"));
            res.IsOk.ShouldBeTrue($"allowed workflow should pass: {res.ErrorMessage}");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    [FactIfPg]
    public async Task Workflow_TicketDisallowedWorkflow_Rejected()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"workflow_shortnames":["approval"]}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Ticket, workflow: "other"));
            res.IsOk.ShouldBeFalse("disallowed workflow should be rejected");
            res.ErrorCode.ShouldBe(InternalErrorCode.INVALID_DATA);
            res.ErrorMessage!.ShouldContain("workflow");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    [FactIfPg]
    public async Task Workflow_NonTicket_IgnoresWorkflowList()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"workflow_shortnames":["approval"]}""");
        try
        {
            // A content entry never carries a workflow — the workflow gate must not fire.
            var res = await validator.ValidateAsync(MakeEntry(space, "/cat", ResourceType.Content));
            res.IsOk.ShouldBeTrue("non-ticket resources ignore workflow_shortnames");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    // ---- structural edges ------------------------------------------------------

    [FactIfPg]
    public async Task NoParentFolder_Unconstrained()
    {
        var (spaces, entries, validator) = Resolve();
        // Seed a folder so the space exists, but validate an entry at "/" (root),
        // which has no parent folder.
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "cat", """{"content_resource_types":["content"]}""");
        try
        {
            var res = await validator.ValidateAsync(MakeEntry(space, "/", ResourceType.Ticket));
            res.IsOk.ShouldBeTrue("entries at a space root have no parent folder to constrain them");
        }
        finally { await spaces.DeleteAsync(space); }
    }

    [FactIfPg]
    public async Task RawPath_Schema_From_Payload_Attrs_Rejected()
    {
        var (spaces, entries, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "feed", """{"content_schema_shortnames":["x"]}""");
        try
        {
            var attrs = new Dictionary<string, object>
            {
                ["payload"] = new Dictionary<string, object> { ["schema_shortname"] = "y" },
            };
            var res = await validator.ValidateRawAsync(space, "/feed", "c1", ResourceType.Comment, attrs);
            res.IsOk.ShouldBeFalse("comment-on-folder with a disallowed schema should be rejected");
            res.ErrorCode.ShouldBe(InternalErrorCode.INVALID_DATA);
            res.ErrorMessage!.ShouldContain("schema");
        }
        finally { await spaces.DeleteAsync(space); }
    }
}
