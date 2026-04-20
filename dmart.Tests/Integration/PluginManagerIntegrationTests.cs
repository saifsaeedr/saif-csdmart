using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end tests that the plugin pipeline wires from the service layer all
// the way down to a registered IHookPlugin instance. We:
//   1. Force the host to construct (so AdminBootstrap runs + PluginManager is
//      loaded from plugins/<name>/config.json on disk).
//   2. Build a test space that opts the plugin in via active_plugins.
//   3. Drive EntryService.CreateAsync and check the plugin's side effect
//      landed in the DB.
//
// Keeping the plugin choice to resource_folders_creation because it touches
// only EntryRepository + Space state we control — no websocket, no auth side
// effects, no Firebase gateway.
public class PluginManagerIntegrationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PluginManagerIntegrationTests(DmartFactory factory) => _factory = factory;

    private (PluginManager plugins, SpaceRepository spaces, EntryService entries, EntryRepository entryRepo)
        Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<PluginManager>(),
            sp.GetRequiredService<SpaceRepository>(),
            sp.GetRequiredService<EntryService>(),
            sp.GetRequiredService<EntryRepository>());
    }

    // ==================== plugin discovery ====================

    [FactIfPg]
    public async Task PluginManager_Loads_ResourceFoldersCreation_From_ConfigJson()
    {
        var (plugins, _, _, _) = Resolve();
        // LoadAsync is called from Program.cs at startup but not from WebApplicationFactory's
        // test host. Load explicitly here so the test host picks up the shipped configs.
        await plugins.LoadAsync();

        plugins.ActivePlugins.ShouldContain("resource_folders_creation");
    }

    // ==================== after-hook dispatch through the service ====================

    [FactIfPg]
    public async Task CreateSpace_Triggers_ResourceFoldersCreation_To_Materialize_Schema_Folder()
    {
        var (plugins, spaces, entries, entryRepo) = Resolve();
        await plugins.LoadAsync();

        var spaceName = $"pitest_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            // Create the test space directly through SpaceRepository (NOT through
            // the plugin pipeline) because we need the space row to exist BEFORE
            // the plugin dispatch fires — otherwise PluginManager's before-action
            // space lookup would return null and skip every hook.
            //
            // We opt this space into the resource_folders_creation plugin via
            // active_plugins. Without that, PluginManager would refuse to fire
            // the hook for events coming from this space.
            await spaces.UpsertAsync(new Space
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = spaceName,
                SpaceName = spaceName,
                Subpath = "/",
                OwnerShortname = "dmart",
                IsActive = true,
                Languages = new() { Language.En },
                ActivePlugins = new() { "resource_folders_creation" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            // Now fire a Folder create via EntryService. The plugin's filters
            // target user + space creates — Folder doesn't match, so this is a
            // sanity negative: no side-effect folder should appear anywhere.
            var probe = new Entry
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = "probe",
                SpaceName = spaceName,
                Subpath = "/",
                ResourceType = ResourceType.Folder,
                IsActive = true,
                OwnerShortname = "dmart",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var result = await entries.CreateAsync(probe, "dmart");
            result.IsOk.ShouldBeTrue($"probe folder create failed: {result.ErrorMessage}");

            // The /schema follow-up is triggered by Space creates — since we
            // upserted the space directly, the plugin didn't fire for the space
            // itself. So instead we fire a synthetic space-create Event against
            // the PluginManager and verify the /schema folder appears. This
            // exercises the AfterActionAsync dispatch surface directly.
            await plugins.AfterActionAsync(new Event
            {
                SpaceName = spaceName,
                Subpath = "/",
                Shortname = spaceName,
                ActionType = ActionType.Create,
                ResourceType = ResourceType.Space,
                UserShortname = "dmart",
            });
            // AfterActionAsync fires concurrent hooks as fire-and-forget — give
            // the background task a moment to land before we check. A small
            // polling loop is more robust than a fixed sleep.
            Entry? schemaFolder = null;
            for (var i = 0; i < 20; i++)
            {
                schemaFolder = await entryRepo.GetAsync(spaceName, "/", "schema", ResourceType.Folder);
                if (schemaFolder is not null) break;
                await Task.Delay(100);
            }
            schemaFolder.ShouldNotBeNull("resource_folders_creation should have materialized /schema");
            // Let the plugin's fire-and-forget background task finish its
            // transaction before we try to delete — avoids a deadlock between
            // the plugin's INSERT and our DELETE.
            await Task.Delay(500);
        }
        finally
        {
            // Retry once on deadlock — the concurrent plugin hook may still
            // be finishing its transaction.
            try { await spaces.DeleteAsync(spaceName); }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "40P01")
            {
                await Task.Delay(500);
                await spaces.DeleteAsync(spaceName);
            }
        }
    }

    // ==================== space active_plugins gating ====================

    [FactIfPg]
    public async Task AfterAction_Skips_Plugins_Not_Listed_In_Space_ActivePlugins()
    {
        var (plugins, spaces, _, entryRepo) = Resolve();
        await plugins.LoadAsync();

        var spaceName = $"pitest2_{Guid.NewGuid():N}".Substring(0, 16);
        try
        {
            // No active_plugins list → plugin should NOT fire for this space.
            await spaces.UpsertAsync(new Space
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = spaceName,
                SpaceName = spaceName,
                Subpath = "/",
                OwnerShortname = "dmart",
                IsActive = true,
                Languages = new() { Language.En },
                ActivePlugins = new(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            await plugins.AfterActionAsync(new Event
            {
                SpaceName = spaceName,
                Subpath = "/",
                Shortname = spaceName,
                ActionType = ActionType.Create,
                ResourceType = ResourceType.Space,
                UserShortname = "dmart",
            });
            // Give any fire-and-forget tasks time to land — we want to verify
            // they DID NOT land, so waiting is load-bearing here.
            await Task.Delay(500);

            var schemaFolder = await entryRepo.GetAsync(spaceName, "/", "schema", ResourceType.Folder);
            schemaFolder.ShouldBeNull("plugin must not fire when space active_plugins doesn't include it");
        }
        finally
        {
            await spaces.DeleteAsync(spaceName);
        }
    }
}
