using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Plugins.BuiltIn;

// Port of dmart/backend/plugins/resource_folders_creation/plugin.py.
//
// On User create  → materialize people/{shortname} + 5 sub-folders in "personal"
// On Space create → materialize /schema in the newly-created space
//
// We write directly through EntryRepository (not EntryService) so the follow-up
// folder creates don't themselves refire the create hook — matches Python's use
// of `db.internal_save_model` which bypasses plugin_manager.
public sealed class ResourceFoldersCreationPlugin(
    EntryRepository entries,
    ILogger<ResourceFoldersCreationPlugin> log) : IHookPlugin
{
    public string Shortname => "resource_folders_creation";

    public async Task HookAsync(Event e, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(e.Shortname) || e.ResourceType is null) return;

        var folders = e.ResourceType switch
        {
            ResourceType.User => new[]
            {
                (Space: "personal", Subpath: "/people", Name: e.Shortname),
                (Space: "personal", Subpath: $"/people/{e.Shortname}", Name: "notifications"),
                (Space: "personal", Subpath: $"/people/{e.Shortname}", Name: "private"),
                (Space: "personal", Subpath: $"/people/{e.Shortname}", Name: "protected"),
                (Space: "personal", Subpath: $"/people/{e.Shortname}", Name: "public"),
                (Space: "personal", Subpath: $"/people/{e.Shortname}", Name: "inbox"),
            },
            ResourceType.Space => new[]
            {
                (Space: e.Shortname, Subpath: "/", Name: "schema"),
            },
            _ => Array.Empty<(string, string, string)>(),
        };

        foreach (var (spaceName, subpath, shortname) in folders)
        {
            var existing = await entries.GetAsync(spaceName, subpath, shortname, ResourceType.Folder, ct);
            if (existing is not null) continue;

            var folder = new Entry
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = spaceName,
                Subpath = subpath,
                ResourceType = ResourceType.Folder,
                IsActive = true,
                OwnerShortname = e.UserShortname,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            try
            {
                await entries.UpsertAsync(folder, ct);
            }
            catch (Exception ex)
            {
                // Missing parent space (especially the "personal" space for user
                // follow-ups) is the most common cause of failure here. Log + skip
                // so one broken space doesn't take down user creation.
                log.LogWarning(ex, "resource_folders_creation: failed to create folder {Space}/{Sub}/{Name}",
                    spaceName, subpath, shortname);
            }
        }
    }
}
