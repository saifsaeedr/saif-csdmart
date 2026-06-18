using Dmart.Auth;
using Dmart.Config;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.DataAdapters.Sql;

// Hosted service that runs after SchemaInitializer. Idempotently:
//   1. Creates the super_admin role + admin user (if config provided + not already there)
//   2. Invalidates the in-process permission cache so stale entries from a prior
//      run don't leak into the newly-booted host
//
// Designed to be safe to leave in production: if the admin shortname is unset,
// the admin-creation step is a no-op but #2 still runs.
public sealed class AdminBootstrap(
    Db db,
    IOptions<DmartSettings> settings,
    UserRepository users,
    AccessRepository access,
    SpaceRepository spaces,
    EntryRepository entries,
    PasswordHasher hasher,
    AuthzCacheRefresher authzRefresher,
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

            // 1. Create the admin user (passwordless — set via `dmart passwd`)
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
                    // Admin is always bootstrapped as English — there's no user-facing
                    // localization that consumes User.Language, so a richer default
                    // would just be cosmetic. Users can change their own language via
                    // POST /user/profile if they need to.
                    Language = Language.En,
                    Type = UserType.Web,
                    IsActive = true,
                    IsEmailVerified = true,
                    CreatedAt = TimeUtils.Now(),
                    UpdatedAt = TimeUtils.Now(),
                };
                await users.UpsertAsync(admin, ct);
                log.LogInformation("admin bootstrap: created admin user {Shortname}", AdminShortname);
            }
            else
            {
                // Repair invariants that, if drifted, become security issues:
                //   * Type=Bot would skip session-row binding entirely
                //     (OnTokenValidated short-circuits for bot users), so a
                //     leaked JWT secret could mint dmart tokens forever.
                //   * IsActive=false would lock out admin recovery.
                //   * super_admin role missing would silently strip privileges.
                //   * Empty stored password — bootstrap seeds the hash from
                //     config.env's AdminPassword on the FIRST start where
                //     the admin row exists but no hash was written yet
                //     (e.g. an external migration created the row). After
                //     that, the stored hash is the source of truth and
                //     `dmart passwd` is how operators rotate it; bootstrap
                //     stops touching the password. This is intentionally
                //     narrower than "rehash whenever config.env disagrees"
                //     — a wider check silently undid every `dmart passwd`
                //     rotation on the next service restart, because
                //     operators rarely also edit config.env's
                //     ADMIN_PASSWORD when rotating live.
                // Fix all four idempotently every startup so corrupted state
                // never persists across restarts.
                var repairs = new List<string>();
                var repaired = existing;
                if (existing.Type != UserType.Web)
                {
                    repaired = repaired with { Type = UserType.Web };
                    repairs.Add($"type {existing.Type}→Web");
                }
                if (!existing.IsActive)
                {
                    repaired = repaired with { IsActive = true };
                    repairs.Add("is_active false→true");
                }
                if (!existing.Roles.Contains("super_admin", StringComparer.Ordinal))
                {
                    repaired = repaired with { Roles = new(existing.Roles) { "super_admin" } };
                    repairs.Add("super_admin role re-attached");
                }
                if (!string.IsNullOrEmpty(s.AdminPassword)
                    && string.IsNullOrEmpty(existing.Password))
                {
                    repaired = repaired with { Password = hasher.Hash(s.AdminPassword) };
                    repairs.Add("password seeded from config (stored hash was empty)");
                }
                if (repairs.Count > 0)
                {
                    await users.UpsertAsync(repaired with { UpdatedAt = TimeUtils.Now() }, ct);
                    log.LogWarning("admin bootstrap: repaired drifted admin invariants: {Repairs}",
                        string.Join("; ", repairs));
                }
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
                    // active_plugins removed: plugins self-declare scope via
                    // their own filters now (see EventFilter.cs).
                    CreatedAt = TimeUtils.Now(),
                    UpdatedAt = TimeUtils.Now(),
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
                    CreatedAt = TimeUtils.Now(),
                    UpdatedAt = TimeUtils.Now(),
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
                    CreatedAt = TimeUtils.Now(),
                    UpdatedAt = TimeUtils.Now(),
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
                    CreatedAt = role?.CreatedAt ?? TimeUtils.Now(),
                    UpdatedAt = TimeUtils.Now(),
                };
                await access.UpsertRoleAsync(role, ct);
                log.LogInformation("admin bootstrap: upserted super_admin role with super_manager permission");
            }

            // 5. Ensure the `logged_in` role row exists. PermissionService
            // injects this role NAME implicitly for every authenticated user,
            // but the implicit grant resolves to nothing unless an actual
            // management/roles row exists — so provision it (empty) on every
            // deployment. CREATE-IF-MISSING ONLY: unlike super_admin above,
            // its permissions list belongs entirely to the operator ("grant X
            // to all logged-in users" = attach X here), so bootstrap must
            // never repair or reset it.
            var loggedIn = await access.GetRoleAsync(
                Services.PermissionService.ImplicitAuthenticatedRole, ct);
            if (loggedIn is null)
            {
                await access.UpsertRoleAsync(new Role
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = Services.PermissionService.ImplicitAuthenticatedRole,
                    SpaceName = MgmtSpace,
                    Subpath = "/roles",
                    OwnerShortname = AdminShortname,
                    Permissions = new(),
                    IsActive = true,
                    Displayname = new Translation(En: "Logged in"),
                    Description = new Translation(
                        En: "Implicitly held by every authenticated user — attach permissions here to grant them to all logged-in users"),
                    CreatedAt = TimeUtils.Now(),
                    UpdatedAt = TimeUtils.Now(),
                }, ct);
                log.LogInformation(
                    "admin bootstrap: created empty logged_in role (implicitly held by every authenticated user)");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "admin bootstrap failed — continuing without admin user");
        }
    }

    private async Task RefreshAuthzAsync(CancellationToken ct)
    {
        // Clear the in-memory permission cache so stale entries from a prior
        // process don't leak into the newly-booted host.
        await authzRefresher.RefreshAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
