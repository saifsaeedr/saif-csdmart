using Dmart.Auth;
using Dmart.Config;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dmart.DataAdapters.Sql;

// Hosted service that runs after SchemaInitializer. Idempotently:
//   1. Creates the super_admin role + admin user (if config provided + not already there)
//   2. Refreshes the authz materialized views so the in-process permission resolver
//      sees fresh data even when the bootstrap is a no-op (existing user case)
//   3. Records an initial count_history snapshot so the analytics table has at least
//      one row right after the host comes up
//
// Designed to be safe to leave in production: if the admin shortname is unset, the
// admin-creation step is a no-op but #2 and #3 still run.
public sealed class AdminBootstrap(
    Db db,
    IOptions<DmartSettings> settings,
    UserRepository users,
    AccessRepository access,
    SpaceRepository spaces,
    EntryRepository entries,
    PasswordHasher hasher,
    AuthzCacheRefresher authzRefresher,
    CountHistoryRepository countHistory,
    ILogger<AdminBootstrap> log) : IHostedService
{
    private const string MgmtSpace = "management";

    public async Task StartAsync(CancellationToken ct)
    {
        // Refuse to start in Production with insecure defaults.
        var s = settings.Value;
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isProduction = env is null or "Production";
        if (s.JwtSecret.Contains("change-me", StringComparison.OrdinalIgnoreCase))
        {
            if (isProduction)
                throw new InvalidOperationException(
                    "JWT_SECRET is still the default value. Set a strong secret (32+ bytes) in config.env before running in production.");
            log.LogCritical("JWT_SECRET is set to the default value — change it before production use!");
        }

        if (!db.IsConfigured) return;
        await BootstrapAdminAsync(ct);
        await RefreshAuthzAsync(ct);
        await SnapshotCountHistoryAsync(ct);
    }

    private const string AdminShortname = "dmart";

    private async Task BootstrapAdminAsync(CancellationToken ct)
    {
        var s = settings.Value;

        try
        {
            // ORDER MATTERS — every table has owner_shortname FK → users.
            // 1. User first (self-referential FK is fine)
            // 2. Space (FK → users via owner_shortname)
            // 3. Entries/folders (FK → users via owner_shortname)
            // 4. Permission (FK → users via owner_shortname)
            // 5. Role (FK → users via owner_shortname)

            // 1. Create the admin user (passwordless — set via `dmart set_password`)
            var existing = await users.GetByShortnameAsync(AdminShortname, ct);
            if (existing is null)
            {
                var admin = new User
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = AdminShortname,
                    SpaceName = MgmtSpace,
                    Subpath = "/users",
                    OwnerShortname = AdminShortname,
                    Email = string.IsNullOrWhiteSpace(s.AdminEmail) ? null : s.AdminEmail,
                    Password = string.IsNullOrEmpty(s.AdminPassword) ? null : hasher.Hash(s.AdminPassword),
                    Roles = new() { "super_admin" },
                    Language = ParseLanguage(s.DefaultLanguage),
                    Type = UserType.Web,
                    IsActive = true,
                    IsEmailVerified = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                await users.UpsertAsync(admin, ct);
                log.LogInformation("admin bootstrap: created admin user {Shortname}", AdminShortname);
            }

            // 2. Ensure the management space exists
            var mgmtSpace = await spaces.GetAsync(MgmtSpace, ct);
            if (mgmtSpace is null)
            {
                mgmtSpace = new Space
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = MgmtSpace,
                    SpaceName = MgmtSpace,
                    Subpath = "/",
                    OwnerShortname = AdminShortname,
                    IsActive = true,
                    Displayname = new Translation(En: "Management"),
                    Description = new Translation(En: "Management space"),
                    Languages = new() { Language.En },
                    ActivePlugins = new() { "resource_folders_creation", "audit" },
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                await spaces.UpsertAsync(mgmtSpace, ct);
                log.LogInformation("admin bootstrap: created management space");
            }

            // 3. Standard folders under management
            foreach (var folder in new[] { "users", "roles", "permissions", "schema" })
            {
                var f = await entries.GetAsync(MgmtSpace, "/", folder, ResourceType.Folder, ct);
                if (f is not null) continue;
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = folder,
                    SpaceName = MgmtSpace,
                    Subpath = "/",
                    ResourceType = ResourceType.Folder,
                    IsActive = true,
                    OwnerShortname = AdminShortname,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }, ct);
            }

            // 4. Ensure the super_manager permission exists (super_admin role
            // attaches it). super_manager grants every action on every resource type
            // across every space + subpath via dmart's __all_spaces__/__all_subpaths__
            // magic words. This matches the row dmart Python writes during its own
            // bootstrap and is what the PermissionService walks at request time.
            var superManager = await access.GetPermissionAsync("super_manager", ct);
            if (superManager is null)
            {
                superManager = new Permission
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = "super_manager",
                    SpaceName = MgmtSpace,
                    Subpath = "/permissions",
                    OwnerShortname = AdminShortname,
                    IsActive = true,
                    Displayname = new Translation(En: "Super Manager"),
                    Description = new Translation(En: "Grants every action on every resource in every space"),
                    Subpaths = new()
                    {
                        [PermissionService.AllSpacesMw] = new() { PermissionService.AllSubpathsMw },
                    },
                    ResourceTypes = Enum.GetValues<ResourceType>()
                        .Select(JsonbHelpers.EnumMember)
                        .ToList(),
                    Actions = new() { "view", "create", "update", "delete", "query", "attach" },
                    Conditions = new(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                await access.UpsertPermissionAsync(superManager, ct);
                log.LogInformation("admin bootstrap: created super_manager permission");
            }

            // Ensure the super_admin role exists and has super_manager attached.
            var role = await access.GetRoleAsync("super_admin", ct);
            var needRoleUpsert = role is null || !role.Permissions.Contains("super_manager");
            if (needRoleUpsert)
            {
                role = new Role
                {
                    Uuid = role?.Uuid ?? Guid.NewGuid().ToString(),
                    Shortname = "super_admin",
                    SpaceName = MgmtSpace,
                    Subpath = "/roles",
                    OwnerShortname = AdminShortname,
                    Permissions = new() { "super_manager" },
                    IsActive = true,
                    Displayname = new Translation(En: "Super Admin"),
                    Description = new Translation(En: "Holds super_manager — grants everything"),
                    CreatedAt = role?.CreatedAt ?? DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                await access.UpsertRoleAsync(role, ct);
                log.LogInformation("admin bootstrap: upserted super_admin role with super_manager permission");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "admin bootstrap failed — continuing without admin user");
        }
    }

    private async Task RefreshAuthzAsync(CancellationToken ct)
    {
        // Always refresh — the user/role rows may have been written by dmart Python
        // since the last C# startup, in which case the materialized views are stale.
        await authzRefresher.RefreshAsync(ct);
    }

    private async Task SnapshotCountHistoryAsync(CancellationToken ct)
    {
        try
        {
            await countHistory.RecordSnapshotForAllSpacesAsync(ct);
            log.LogDebug("count_history initial snapshot recorded");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "count_history initial snapshot failed");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    // Accepts ISO codes ("en"/"ar"/"ku") and dmart's full forms ("english"/"arabic"...).
    private static Language ParseLanguage(string code) => code?.ToLowerInvariant() switch
    {
        "ar" or "arabic"  => Language.Ar,
        "ku" or "kurdish" => Language.Ku,
        "fr" or "french"  => Language.Fr,
        "tr" or "turkish" => Language.Tr,
        _                 => Language.En,
    };
}
