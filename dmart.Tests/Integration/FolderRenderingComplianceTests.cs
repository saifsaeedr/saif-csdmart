using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Folder-rendering compliance machinery:
//   * the folder_rendering schema is CENTRALIZED — SchemaValidator always
//     resolves it from the management space, whatever space the folder lives
//     in, so one canonical (strict) definition governs every folder body;
//   * `dmart check` reports non-compliant content via the
//     folder_content_violations health check, so legacy data that predates
//     enforcement is visible instead of silently tolerated.
public class FolderRenderingComplianceTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public FolderRenderingComplianceTests(DmartFactory factory) => _factory = factory;

    // Make sure management carries the canonical folder_rendering schema (the
    // live test DB normally has it from seeding; create a minimal strict one
    // when absent and report whether we own the cleanup).
    private async Task<bool> EnsureManagementSchemaAsync(EntryRepository entries)
    {
        foreach (var sub in new[] { "/schema", "/schemas" })
            if (await entries.GetAsync("management", sub, "folder_rendering", ResourceType.Schema) is not null)
                return false;

        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "folder_rendering", SpaceName = "management", Subpath = "/schema",
            ResourceType = ResourceType.Schema, IsActive = true, OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(
                    """{"type":"object","additionalProperties":false,"properties":{"content_resource_types":{"type":"array","items":{"type":"string"}}}}""").RootElement.Clone(),
            },
        });
        return true;
    }

    [FactIfPg]
    public async Task FolderRendering_Schema_Resolves_From_Management_For_Any_Space()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        var entries = sp.GetRequiredService<EntryRepository>();
        var schemas = sp.GetRequiredService<SchemaValidator>();
        var seededSchema = await EnsureManagementSchemaAsync(entries);
        schemas.ClearCache();
        try
        {
            // A space with NO local folder_rendering schema: validation must
            // still bite, via the management copy — additionalProperties:false
            // there must reject a junk field in this body.
            var junkBody = JsonDocument.Parse("""{"definitely_not_a_folder_rendering_field":1}""").RootElement.Clone();
            var errors = await schemas.ValidateAsync(
                "space_with_no_schemas_" + Guid.NewGuid().ToString("N")[..6],
                "folder_rendering", junkBody);
            errors.ShouldNotBeNull(
                "folder_rendering must resolve from the management space even when the folder's own space has no copy");
        }
        finally
        {
            if (seededSchema)
            {
                try { await entries.DeleteAsync("management", "/schema", "folder_rendering", ResourceType.Schema); } catch { }
                schemas.ClearCache();
            }
        }
    }

    [FactIfPg]
    public async Task HealthCheck_Reports_Folder_Content_Violations()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        var spaces = sp.GetRequiredService<SpaceRepository>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var health = sp.GetRequiredService<HealthCheckRepository>();

        var spaceName = $"fcc_{Guid.NewGuid():N}"[..16];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = "dmart", IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        // Folder restricted to content; seed one compliant and one violating
        // child DIRECTLY via the repo (mimicking legacy rows that predate
        // enforcement — the write-path gate never sees them).
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "locked", SpaceName = spaceName, Subpath = "/",
            ResourceType = ResourceType.Folder, IsActive = true, OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse("""{"content_resource_types":["content"]}""").RootElement.Clone(),
            },
        });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "ok_child", SpaceName = spaceName, Subpath = "/locked",
            ResourceType = ResourceType.Content, IsActive = true, OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "bad_child", SpaceName = spaceName, Subpath = "/locked",
            ResourceType = ResourceType.Ticket, IsActive = true, OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            var results = await health.RunAsync(spaceName, "all");
            var check = results.SingleOrDefault(r => r.Name == "folder_content_violations");
            check.ShouldNotBeNull("`dmart check` must report folder content compliance");
            check!.Count.ShouldBe(1);
            check.Samples.ShouldContain(s => s.Contains("bad_child"));
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }
}
