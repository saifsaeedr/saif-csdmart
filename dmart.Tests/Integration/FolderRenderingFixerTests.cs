using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.Cli;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// `dmart fix-folder-rendering` — auto-repair of pre-existing folder payload
// bodies so ENFORCE_FOLDER_CONTENT_POLICY (and the strict centralized
// folder_rendering schema) can be enabled without breaking legacy spaces:
//   * strips body fields the canonical schema doesn't define
//   * adds required-but-missing fields with neutral defaults
//   * widens non-empty policy arrays to cover the folder's existing children
// Content is never modified; folder bodies only.
public class FolderRenderingFixerTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public FolderRenderingFixerTests(DmartFactory factory) => _factory = factory;

    // The fixer needs the canonical schema in management (CI runs against a
    // freshly-migrated DB that may not be seeded). Create a minimal strict one
    // when absent; returns true when this test owns the cleanup.
    private static async Task<bool> EnsureManagementSchemaAsync(EntryRepository entries)
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
                Body = JsonDocument.Parse("""
                    {"type":"object","additionalProperties":false,
                     "properties":{
                       "content_resource_types":{"type":"array","items":{"type":"string"}},
                       "content_schema_shortnames":{"type":"array","items":{"type":"string"}},
                       "workflow_shortnames":{"type":"array","items":{"type":"string"}},
                       "index_attributes":{"type":"array"}},
                     "required":["index_attributes"]}
                    """).RootElement.Clone(),
            },
        });
        return true;
    }

    [FactIfPg]
    public async Task Fixer_Repairs_Legacy_Folder_Body_And_Blesses_Existing_Children()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        var spaces = sp.GetRequiredService<SpaceRepository>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var health = sp.GetRequiredService<HealthCheckRepository>();
        var fixer = new FolderRenderingFixer(sp.GetRequiredService<Db>());
        var seededSchema = await EnsureManagementSchemaAsync(entries);

        var spaceName = $"ffx_{Guid.NewGuid():N}"[..16];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = "dmart", IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        // Legacy folder: junk field, missing required index_attributes, and a
        // policy that doesn't cover the children that already live inside.
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "legacy", SpaceName = spaceName, Subpath = "/",
            ResourceType = ResourceType.Folder, IsActive = true, OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(
                    """{"definitely_junk_field":1,"content_resource_types":["content"],"workflow_shortnames":["approval"]}""").RootElement.Clone(),
            },
        });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "doc1", SpaceName = spaceName, Subpath = "/legacy",
            ResourceType = ResourceType.Content, IsActive = true, OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "tk1", SpaceName = spaceName, Subpath = "/legacy",
            ResourceType = ResourceType.Ticket, IsActive = true, OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            WorkflowShortname = "other_flow",
        });

        try
        {
            // Dry-run: plan the fixes without writing.
            var plan = await fixer.ScanAsync(spaceName);
            var fix = plan.SingleOrDefault(f => f.Path == "//legacy" || f.Path == "/legacy");
            fix.ShouldNotBeNull("the legacy folder must be flagged for fixing");
            fix!.RemovedFields.ShouldContain("definitely_junk_field");
            fix.AddedRequired.ShouldContain("index_attributes");
            fix.WidenedArrays.Keys.ShouldContain("content_resource_types");
            fix.WidenedArrays["content_resource_types"].ShouldContain("ticket");
            fix.WidenedArrays.Keys.ShouldContain("workflow_shortnames");
            fix.WidenedArrays["workflow_shortnames"].ShouldContain("other_flow");

            // Dry-run must not have written anything.
            var untouched = await entries.GetAsync(spaceName, "/", "legacy", ResourceType.Folder);
            untouched!.Payload!.Body!.Value.TryGetProperty("definitely_junk_field", out _)
                .ShouldBeTrue("dry-run must not modify the folder");

            // Apply, then everything is compliant.
            var applied = await fixer.ApplyAsync(spaceName);
            applied.ShouldBe(1);

            var fixedFolder = await entries.GetAsync(spaceName, "/", "legacy", ResourceType.Folder);
            var body = fixedFolder!.Payload!.Body!.Value;
            body.TryGetProperty("definitely_junk_field", out _).ShouldBeFalse("junk field stripped");
            body.TryGetProperty("index_attributes", out var ia).ShouldBeTrue("required field added");
            ia.ValueKind.ShouldBe(JsonValueKind.Array);
            body.GetProperty("content_resource_types").EnumerateArray()
                .Select(e => e.GetString()).ShouldContain("ticket");
            body.GetProperty("workflow_shortnames").EnumerateArray()
                .Select(e => e.GetString()).ShouldContain("other_flow");

            // The policy violations are gone and a re-scan finds nothing.
            var checks = await health.RunAsync(spaceName, "soft");
            checks.Single(c => c.Name == "folder_content_violations").Count.ShouldBe(0);
            (await fixer.ScanAsync(spaceName)).ShouldBeEmpty("re-scan after apply must be clean");
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
            if (seededSchema)
                try { await entries.DeleteAsync("management", "/schema", "folder_rendering", ResourceType.Schema); } catch { }
        }
    }
}
