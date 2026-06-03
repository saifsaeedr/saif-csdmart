# Role-Grant Delegation (`grantable_by`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a global admin delegate role-assignment authority by adding `grantable_by` to roles, so a non-admin user-manager may assign roles they don't personally hold — without escalation.

**Architecture:** A role gains a nullable `grantable_by` JSONB column (a list of role shortnames whose holders may assign that role). The managed user-write privilege floor (`EnforcePrivilegeFloorAsync`) is rewritten: the super-admin bypass stays, the "assign a role you hold" allowance is **removed**, and a non-admin may assign role `R` only if `R.grantable_by` intersects the actor's roles. Setting `grantable_by` is global-admin-only. Groups, `/user/create`, and OAuth are untouched.

**Tech Stack:** C# / .NET 10, Npgsql + PostgreSQL (JSONB), source-gen System.Text.Json, xUnit + Shouldly integration tests (`[FactIfPg]`).

**Spec:** `docs/superpowers/specs/2026-06-03-role-grant-delegation-design.md`

---

## File structure (what each change is responsible for)

- `Dmart.Models/Core/Role.cs` — add the `GrantableBy` model field.
- `DataAdapters/Sql/SqlSchema.cs` — `roles.grantable_by JSONB` in CREATE TABLE + idempotent ALTER migration.
- `DataAdapters/Sql/ExpectedColumnPatcher.cs` — startup backstop that the column exists.
- `DataAdapters/Sql/AccessRepository.cs` — persist/read the column (`SelectRoleColumns`, `HydrateRole`, `UpsertRoleAsync`).
- `Services/PermissionService.cs` — `NonGrantableRolesAsync`, the pure delegation check (reusable + directly testable).
- `Api/Managed/RequestHandler.cs` — read `grantable_by` on role create/update; floor gates its set and enforces it on user writes.
- `dmart.Tests/Integration/PermissionServiceIntegrationTests.cs` — round-trip + `NonGrantableRolesAsync` unit-level coverage.
- `dmart.Tests/Integration/AuthzRegressionTests.cs` — end-to-end floor allow/deny + gate, as a non-admin.

## Test commands (used throughout)

Reset the admin login lock if a prior run tripped it, then run a filtered test:
```bash
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart -c "UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';"
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' \
DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --filter "<FILTER>" --nologo
```
The test host runs `SchemaInitializer` on startup, which applies the `ALTER TABLE … ADD COLUMN IF NOT EXISTS` migration — so once Task 1 lands, the test database has `roles.grantable_by`.

---

## Task 1: `grantable_by` model + schema + repository round-trip

**Files:**
- Modify: `Dmart.Models/Core/Role.cs:31` (add field after `Permissions`)
- Modify: `DataAdapters/Sql/SqlSchema.cs` (CREATE TABLE roles ~line 116; migration ~line 405)
- Modify: `DataAdapters/Sql/ExpectedColumnPatcher.cs` (roles block ~line 24)
- Modify: `DataAdapters/Sql/AccessRepository.cs` (`SelectRoleColumns` ~line 16; `UpsertRoleAsync` ~line 76; `HydrateRole` ~line 417)
- Test: `dmart.Tests/Integration/PermissionServiceIntegrationTests.cs`

- [ ] **Step 1: Write the failing round-trip test**

Add to `PermissionServiceIntegrationTests.cs` (it already has `Resolve()`, `BuildRole`, and `[FactIfPg]`):

```csharp
[FactIfPg]
public async Task GrantableBy_RoundTrips_Through_Upsert_And_Hydrate()
{
    var (_, _, access) = Resolve();
    var roleName = $"gb_rt_{Guid.NewGuid():N}"[..18];
    try
    {
        await access.UpsertRoleAsync(
            BuildRole(roleName) with { GrantableBy = new() { "user_manager", "team_lead" } });
        var read = await access.GetRoleAsync(roleName);
        read.ShouldNotBeNull();
        read!.GrantableBy.ShouldBe(new[] { "user_manager", "team_lead" });

        // null grantable_by round-trips as null (not empty list)
        await access.UpsertRoleAsync(BuildRole(roleName));
        (await access.GetRoleAsync(roleName))!.GrantableBy.ShouldBeNull();
    }
    finally { try { await access.DeleteRoleAsync(roleName); } catch { } }
}
```

- [ ] **Step 2: Run the test to verify it fails (compile error)**

```bash
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --filter "FullyQualifiedName~GrantableBy_RoundTrips" --nologo
```
Expected: FAIL — `'Role' does not contain a definition for 'GrantableBy'`.

- [ ] **Step 3: Add the model field**

In `Dmart.Models/Core/Role.cs`, in the `// ----- Roles-specific -----` block, after `public List<string> Permissions { get; init; } = new();`:

```csharp
    // Role shortnames whose holders may assign THIS role to a user (the
    // managed privilege floor's only non-admin path). null/empty ⇒ only a
    // global admin may grant it. Setting it is global-admin-only.
    public List<string>? GrantableBy { get; init; }
```

- [ ] **Step 4: Add the schema column + migration**

In `DataAdapters/Sql/SqlSchema.cs`, in `CREATE TABLE IF NOT EXISTS roles (...)`, insert before the `permissions             JSONB,` line:

```sql
        grantable_by            JSONB,
```

And in the migrations block, after `ALTER TABLE roles       ADD COLUMN IF NOT EXISTS last_checksum_history TEXT;` (line ~405), add:

```sql
    ALTER TABLE roles       ADD COLUMN IF NOT EXISTS grantable_by          JSONB;
```

- [ ] **Step 5: Add the ExpectedColumnPatcher backstop**

In `DataAdapters/Sql/ExpectedColumnPatcher.cs`, change the `["roles"]` block to:

```csharp
        ["roles"] =
        [
            ("last_checksum_history", "TEXT"),
            ("grantable_by", "JSONB"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        ],
```

- [ ] **Step 6: Persist + read the column in AccessRepository**

In `DataAdapters/Sql/AccessRepository.cs`:

(a) `SelectRoleColumns` — change the last line `last_checksum_history, resource_type, permissions, query_policies` to:
```
               last_checksum_history, resource_type, permissions, query_policies, grantable_by
```

(b) `UpsertRoleAsync` INSERT — add `grantable_by` as the final column and `$21`:
```
            INSERT INTO roles (uuid, shortname, space_name, subpath, is_active, slug,
                               displayname, description, tags, created_at, updated_at,
                               owner_shortname, owner_group_shortname, acl, payload, relationships,
                               last_checksum_history, resource_type, permissions, query_policies,
                               grantable_by)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21)
```
In the `ON CONFLICT … DO UPDATE SET` list, after `query_policies = EXCLUDED.query_policies` add `,` and a new line:
```
                query_policies = EXCLUDED.query_policies,
                grantable_by = EXCLUDED.grantable_by
```
Then, immediately after the `QueryPolicies` parameter block (the one ending `NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, });`), add the `$21` parameter:
```csharp
        AddJsonb(cmd, JsonbHelpers.ToJsonb(role.GrantableBy));
```

(c) `HydrateRole` — after `QueryPolicies = r.IsDBNull(19) ? new() : ((string[])r.GetValue(19)).ToList(),` add:
```csharp
            GrantableBy = JsonbHelpers.FromListString(r.IsDBNull(20) ? null : r.GetString(20)),
```

- [ ] **Step 7: Run the test to verify it passes**

```bash
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart -c "UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';"
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --filter "FullyQualifiedName~GrantableBy_RoundTrips" --nologo
```
Expected: PASS (1 passed, 0 skipped — confirms PG ran it).

- [ ] **Step 8: Commit**

```bash
git add Dmart.Models/Core/Role.cs DataAdapters/Sql/SqlSchema.cs DataAdapters/Sql/ExpectedColumnPatcher.cs DataAdapters/Sql/AccessRepository.cs dmart.Tests/Integration/PermissionServiceIntegrationTests.cs
git commit -m "feat(roles): add grantable_by column (model + schema + repo round-trip)"
```

---

## Task 2: `PermissionService.NonGrantableRolesAsync` (the delegation check)

**Files:**
- Modify: `Services/PermissionService.cs` (new public method; class is `PermissionService(UserRepository users, AccessRepository access, AuthzCacheRefresher cache)`, so `access.GetRolesAsync` is in scope)
- Test: `dmart.Tests/Integration/PermissionServiceIntegrationTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `PermissionServiceIntegrationTests.cs`:

```csharp
[FactIfPg]
public async Task NonGrantableRolesAsync_Flags_Roles_Not_Delegated_To_Actor()
{
    var (perms, _, access) = Resolve();
    var editor = $"gb_ed_{Guid.NewGuid():N}"[..18];
    var secret = $"gb_se_{Guid.NewGuid():N}"[..18];
    try
    {
        // editor is delegated to anyone holding "user_manager"; secret is not delegated.
        await access.UpsertRoleAsync(BuildRole(editor) with { GrantableBy = new() { "user_manager" } });
        await access.UpsertRoleAsync(BuildRole(secret)); // GrantableBy == null
        await access.InvalidateAllCachesAsync();

        // Actor holds user_manager → may grant editor, may NOT grant secret.
        (await perms.NonGrantableRolesAsync(new[] { editor, secret }, new[] { "user_manager" }))
            .ShouldBe(new[] { secret });

        // Actor holds something unrelated → may grant neither.
        (await perms.NonGrantableRolesAsync(new[] { editor }, new[] { "nobody" }))
            .ShouldBe(new[] { editor });

        // Holding the role itself no longer implies grant rights (own-roles removed).
        (await perms.NonGrantableRolesAsync(new[] { editor }, new[] { editor }))
            .ShouldBe(new[] { editor });

        // Empty request → empty result.
        (await perms.NonGrantableRolesAsync(System.Array.Empty<string>(), new[] { "user_manager" }))
            .ShouldBeEmpty();
    }
    finally
    {
        try { await access.DeleteRoleAsync(editor); } catch { }
        try { await access.DeleteRoleAsync(secret); } catch { }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --filter "FullyQualifiedName~NonGrantableRolesAsync_Flags" --nologo
```
Expected: FAIL — `'PermissionService' does not contain a definition for 'NonGrantableRolesAsync'`.

- [ ] **Step 3: Implement the method**

In `Services/PermissionService.cs`, add (near `GetAllowedRolesAsync`-style helpers; `System.Linq` is already imported by the file's existing LINQ use):

```csharp
    // Of `requestedRoles`, returns the subset the actor (holding `actorRoles`) may
    // NOT assign. A role is assignable iff its grantable_by lists a role the actor
    // holds. A role missing from the DB, or with null/empty grantable_by, is
    // non-grantable. Order/duplicates of requestedRoles are preserved in the result.
    // The managed privilege floor calls this only for NON-global-admins (super
    // admins bypass the floor before reaching it).
    public async Task<List<string>> NonGrantableRolesAsync(
        IReadOnlyList<string> requestedRoles,
        IReadOnlyCollection<string> actorRoles,
        CancellationToken ct = default)
    {
        if (requestedRoles.Count == 0) return new();
        var roles = await access.GetRolesAsync(requestedRoles, ct);
        var byName = roles.ToDictionary(r => r.Shortname, StringComparer.Ordinal);
        var actorSet = new HashSet<string>(actorRoles, StringComparer.Ordinal);
        var notAllowed = new List<string>();
        foreach (var rn in requestedRoles)
        {
            var grantable = byName.TryGetValue(rn, out var role) ? role.GrantableBy : null;
            if (grantable is null || !grantable.Any(actorSet.Contains))
                notAllowed.Add(rn);
        }
        return notAllowed;
    }
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --filter "FullyQualifiedName~NonGrantableRolesAsync_Flags" --nologo
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/PermissionService.cs dmart.Tests/Integration/PermissionServiceIntegrationTests.cs
git commit -m "feat(perms): NonGrantableRolesAsync — grantable_by delegation check"
```

---

## Task 3: Wire the setter, gate it, and enforce on user writes (the floor)

**Files:**
- Modify: `Api/Managed/RequestHandler.cs` — `CreateRoleAsync` (~line 485), role UPDATE branch (~line 807), `EnforcePrivilegeFloorAsync` (gate ~line 1386; enforcement ~line 1402-1414)
- Test: `dmart.Tests/Integration/AuthzRegressionTests.cs`

- [ ] **Step 1: Write the failing end-to-end test (allow + deny + gate, as a non-admin)**

Add to `AuthzRegressionTests.cs` (it already has `_factory`, `CreateUserAsync`, `LoginAsAsync`, `Unique`, and `access.DeleteRoleAsync`/`DeletePermissionAsync`). This test sets up a `user_manager` who may create users, delegates `editor` to `user_manager`, leaves `secret` undelegated, and logs in as the manager:

```csharp
[FactIfPg]
public async Task GrantableBy_Governs_Role_Assignment_On_Managed_User_Create()
{
    _factory.CreateClient();
    var users  = _factory.Services.GetRequiredService<UserRepository>();
    var access = _factory.Services.GetRequiredService<AccessRepository>();
    var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

    var mgr      = Unique("gb_mgr");
    var mgrRole  = Unique("gb_mgrrole");
    var mgrPerm  = Unique("gb_mgrperm");
    var editor   = Unique("gb_editor");
    var secret   = Unique("gb_secret");
    var newUserOk   = Unique("gb_newok");
    var newUserDeny = Unique("gb_newdeny");
    var now = DateTime.UtcNow;

    // Manager holds mgrRole, which carries a permission to create/update users
    // under management/users.
    await access.UpsertPermissionAsync(new Permission
    {
        Uuid = Guid.NewGuid().ToString(), Shortname = mgrPerm,
        SpaceName = "management", Subpath = "/permissions", OwnerShortname = "dmart", IsActive = true,
        Subpaths = new() { ["management"] = new() { "users" } },
        ResourceTypes = new() { "user" },
        Actions = new() { "create", "update" },
        CreatedAt = now, UpdatedAt = now,
    });
    await access.UpsertRoleAsync(new Role
    {
        Uuid = Guid.NewGuid().ToString(), Shortname = mgrRole,
        SpaceName = "management", Subpath = "/roles", OwnerShortname = "dmart", IsActive = true,
        Permissions = new() { mgrPerm }, CreatedAt = now, UpdatedAt = now,
    });
    // editor is delegated to mgrRole holders; secret is not delegated.
    await access.UpsertRoleAsync(new Role
    {
        Uuid = Guid.NewGuid().ToString(), Shortname = editor,
        SpaceName = "management", Subpath = "/roles", OwnerShortname = "dmart", IsActive = true,
        GrantableBy = new() { mgrRole }, CreatedAt = now, UpdatedAt = now,
    });
    await access.UpsertRoleAsync(new Role
    {
        Uuid = Guid.NewGuid().ToString(), Shortname = secret,
        SpaceName = "management", Subpath = "/roles", OwnerShortname = "dmart", IsActive = true,
        CreatedAt = now, UpdatedAt = now,
    });
    // CreateUserAsync already accepts a roles list (see its signature in this file).
    await CreateUserAsync(users, hasher, mgr, new List<string> { mgrRole });
    await access.InvalidateAllCachesAsync();

    try
    {
        var (client, _) = await LoginAsAsync(mgr);

        // ALLOW: editor is grantable to mgrRole holders.
        var ok = await PostManaged(client, RequestType.Create, ResourceType.User, "/users", newUserOk,
            new() { ["roles"] = new List<string> { editor }, ["is_active"] = true });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

        // DENY: secret is not delegated to the manager. A failed record makes the
        // managed request an aggregate failure → HTTP 400 (Python parity).
        var deny = await PostManaged(client, RequestType.Create, ResourceType.User, "/users", newUserDeny,
            new() { ["roles"] = new List<string> { secret }, ["is_active"] = true });
        deny.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await deny.Content.ReadAsStringAsync());

        // GATE: a non-global-admin may not set grantable_by on a role.
        var gate = await PostManaged(client, RequestType.Update, ResourceType.Role, "/roles", editor,
            new() { ["grantable_by"] = new List<string> { mgrRole, secret } });
        gate.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await gate.Content.ReadAsStringAsync());
    }
    finally
    {
        try { await users.DeleteAllSessionsAsync(mgr); } catch { }
        try { await users.DeleteAsync(mgr); } catch { }
        try { await users.DeleteAsync(newUserOk); } catch { }
        try { await users.DeleteAsync(newUserDeny); } catch { }
        try { await access.DeleteRoleAsync(mgrRole); } catch { }
        try { await access.DeleteRoleAsync(editor); } catch { }
        try { await access.DeleteRoleAsync(secret); } catch { }
        try { await access.DeletePermissionAsync(mgrPerm); } catch { }
        await access.InvalidateAllCachesAsync();
    }
}

// Helper: POST a single-record /managed/request. Uses the same Request/Record
// wire shape and source-gen serializer as RolePermissionRequestTests.PostRequest.
// AuthzRegressionTests already imports Dmart.Models.Api, Dmart.Models.Json, and
// System.Net.Http.Json, so no new usings are needed.
private static Task<HttpResponseMessage> PostManaged(
    HttpClient client, RequestType rt, ResourceType resource, string subpath,
    string shortname, Dictionary<string, object> attrs)
{
    var req = new Request
    {
        RequestType = rt,
        SpaceName = "management",
        Records = new() { new() { ResourceType = resource, Subpath = subpath, Shortname = shortname, Attributes = attrs } },
    };
    return client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart -c "UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';"
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --filter "FullyQualifiedName~GrantableBy_Governs_Role_Assignment" --nologo
```
Expected: FAIL — ALLOW case returns 400 (the current floor rejects `editor` because the manager doesn't hold it), and the GATE case returns 200 (the floor doesn't yet gate `grantable_by`).

- [ ] **Step 3: Read `grantable_by` on role create**

In `Api/Managed/RequestHandler.cs::CreateRoleAsync`, in the `new Role { … }` initializer, after `Permissions = ExtractStringList(attrs, "permissions") ?? new(),` add:

```csharp
            GrantableBy = ExtractStringList(attrs, "grantable_by"),
```

- [ ] **Step 4: Presence-gate `grantable_by` on role update**

In the `ResourceType.Role` update branch's `existing with { … }` (the block starting `Permissions = ExtractStringList(attrs, "permissions") ?? existing.Permissions,`), add:

```csharp
                    GrantableBy = attrs.ContainsKey("grantable_by")
                        ? ExtractStringList(attrs, "grantable_by")
                        : existing.GrantableBy,
```

- [ ] **Step 5: Gate `grantable_by` to global admins in the floor**

In `EnforcePrivilegeFloorAsync`, change the `setsRolePerms` line:

```csharp
        var setsRolePerms     = resourceType == ResourceType.Role
                                && (attrs.ContainsKey("permissions") || attrs.ContainsKey("grantable_by"));
```

- [ ] **Step 6: Rewrite the floor's role enforcement (remove own-roles; use grantable_by)**

In `EnforcePrivilegeFloorAsync`, replace the block from `// User roles/groups: a non-admin may only assign values they themselves` down to and including the `return null;` at the end of the method with:

```csharp
        // Roles: the "assign a role you already hold" allowance is intentionally
        // gone — a non-admin may assign a role ONLY when the role's grantable_by
        // lists a role the actor holds (see PermissionService.NonGrantableRolesAsync).
        // Groups: unchanged — a non-admin may still assign groups they belong to.
        var self = await users.GetByShortnameAsync(actor, ct);
        var ownRoles  = self?.Roles  ?? new List<string>();
        var ownGroups = self?.Groups ?? new List<string>();

        var requestedRoles = ExtractStringList(attrs, "roles") ?? new();
        var disallowed = await perms.NonGrantableRolesAsync(requestedRoles, ownRoles, ct);
        disallowed.AddRange((ExtractStringList(attrs, "groups") ?? new())
            .Where(g => !ownGroups.Contains(g, StringComparer.Ordinal)));

        if (disallowed.Count > 0)
            return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                $"not permitted to assign role/group: {string.Join(", ", disallowed)}",
                ErrorTypes.Request);
        return null;
```

- [ ] **Step 7: Run the end-to-end test to verify it passes**

```bash
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart -c "UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';"
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --filter "FullyQualifiedName~GrantableBy_Governs_Role_Assignment" --nologo
```
Expected: PASS (ALLOW=200, DENY=400, GATE=400).

- [ ] **Step 8: Commit**

```bash
git add Api/Managed/RequestHandler.cs dmart.Tests/Integration/AuthzRegressionTests.cs
git commit -m "feat(authz): enforce grantable_by on user-role assignment; gate its setter"
```

---

## Task 4: Reconcile existing tests + full verification

**Files:**
- Modify: any existing test that asserts the old "assign a role you hold" floor behavior.
- Test: full unit + integration suite.

- [ ] **Step 1: Find tests that relied on the removed own-roles allowance**

```bash
grep -rn "cannot assign a role/group you do not hold\|ownRoles\|assign.*role.*you.*hold" --include="*.cs" dmart.Tests/
grep -rln "ResourceType.User" --include="*.cs" dmart.Tests/Integration | xargs grep -ln "roles" 2>/dev/null
```
For each hit where a **non-global-admin** creates/updates a user with a role they *hold* and expects success, update it: either (a) add a delegating role with `grantable_by` set, or (b) flip the expectation to `BadRequest` and rename the test to reflect the new contract. Admin-actor (`dmart`) cases need no change (super-admin bypass).

- [ ] **Step 2: Run the focused authz + permission suites**

```bash
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart -c "UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';"
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --filter "FullyQualifiedName~Authz|FullyQualifiedName~PermissionService|FullyQualifiedName~RolePermission" --nologo
```
Expected: all PASS, 0 skipped.

- [ ] **Step 3: Run the full suite**

```bash
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart -c "UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';"
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj --nologo
```
Expected: all PASS. If a `/user/create` self-registration test exists, confirm it still passes (the default-role path is outside the floor and must be unaffected).

- [ ] **Step 4: Commit any test reconciliation**

```bash
git add -A dmart.Tests/
git commit -m "test: reconcile floor tests with grantable_by-only role assignment"
```

---

## Notes for the implementer
- **Backward compatibility is a deliberate tightening.** After this lands, a non-admin who could previously assign roles they hold can assign nothing until an admin populates `grantable_by`. That is intended; surface it in the PR description as a rollout action.
- **Out of scope (do not add):** a `grantable_by` analog for groups; `grantable_by` referencing permissions; any change to `/user/create` or OAuth (verified unaffected in the spec).
- **Column index discipline:** `grantable_by` must be the **last** column in `SelectRoleColumns` so `HydrateRole` index 20 stays correct. If you reorder columns, update the index.
