# Design: role-grant delegation via `grantable_by`

**Date:** 2026-06-03
**Status:** Design approved, pending spec review → implementation plan
**Supersedes:** the approach in PR #90 (`allowed_roles` on permissions)

## Context

The managed user-write path (`POST /managed/request`, `resource_type=user`) enforces a
privilege floor in `Api/Managed/RequestHandler.cs::EnforcePrivilegeFloorAsync`. Today a
non-global-admin may only assign roles/groups they **personally hold**. That blocks a
legitimate delegation: an admin who holds, say, a `user_manager` role and has create/update
permission on user entries under `management/users`, but who needs to create users with roles
they do *not* hold (e.g. `content_editor`) — always lower-privileged, non-overlapping roles.

PR #90 tried to solve this by adding an `allowed_roles` allowlist on the **permission**. Review
found three problems: it isn't settable through any API or import (consumption-only), it isn't
gated in the floor (a latent self-escalation hole once a setter is added), and it ships no
tests. It also introduces a second authorization concept alongside the permission engine.

This design replaces that approach with a role-centric, explicitly-delegated alternative.

## Decision summary

- **Mechanism:** `grantable_by` on the **role** — a list of role shortnames whose holders may
  assign *this* role to a user. (The grantee role declares who may hand it out.)
- **Grantor identity:** matched by **named role held** (not by raw permission).
- **Super-admin compatibility:** preserved unchanged — an effective global admin
  (`IsGlobalAdminAsync`) bypasses the floor entirely and may assign any role, with no
  `grantable_by` entry required.
- **Own-roles allowance: removed.** Holding a role no longer implies you may grant it.
  Explicit `grantable_by` delegation (or super-admin) is the *only* non-admin path.
- **Groups:** unchanged (a non-admin may still assign groups they belong to). No
  `grantable_by` for groups in this iteration.
- **Setting `grantable_by`:** global-admin-only, wired through the managed role create/update.

## Scope boundary (verified against the code)

The floor — and therefore `grantable_by` — governs **only the managed/admin user-write path**.
Other user-creation paths assign roles differently and are unaffected:

- **`/user/create` (self-registration):** `Services/UserService.cs` deliberately ignores
  caller-supplied `roles`/`groups` and assigns only the configured `UserCreateDefaultRole`
  server-side. It never calls the floor. **Unaffected.**
- **OAuth `/oauth/register`:** RFC 7591 dynamic *client* registration (redirect URIs,
  `client_id`) — not user creation. No user, no roles, no floor. **Unaffected.**
- **OAuth login (Google/Apple/Facebook):** `Auth/OAuth/OAuthUserResolver.cs` resolves
  *existing* users only — "OAuth login no longer auto-creates accounts" — and the refresh path
  mutates only email/avatar/provider-id, never roles. **Unaffected.**

The only role-on-write path the change touches is the managed `RequestHandler`, where an
authenticated actor hand-picks roles — exactly what should be gated.

## Design

### Data model
- Add `GrantableBy : List<string>?` to `Dmart.Models/Core/Role.cs` (beside `Permissions`).
- Semantics: role shortnames whose holders may grant this role. `null`/empty ⇒ only a
  super-admin may grant it.

### Schema (`roles.grantable_by JSONB`, nullable)
Mirror the existing nullable-JSONB plumbing used for `permissions.allowed_fields_values`:
- `DataAdapters/Sql/SqlSchema.cs`: add the column to `CREATE TABLE roles` and an
  `ALTER TABLE roles ADD COLUMN IF NOT EXISTS grantable_by JSONB` migration line.
- `DataAdapters/Sql/ExpectedColumnPatcher.cs`: add `("grantable_by", "JSONB")` under `roles`.
- `DataAdapters/Sql/AccessRepository.cs`: append `grantable_by` last in `SelectRoleColumns`;
  read it in `HydrateRole` via `JsonbHelpers.FromListString` at the new trailing index; write
  it in `UpsertRoleAsync` via `JsonbHelpers.ToJsonb(p.GrantableBy)` as the last parameter.
  `ToJsonb(List<string>?)`/`FromListString` already exist and are null-safe.

### Setter (managed role create/update)
- Create (`CreateRoleAsync`): `GrantableBy = ExtractStringList(attrs, "grantable_by")`.
- Update (role branch's `existing with { … }`): presence-gate —
  `attrs.ContainsKey("grantable_by") ? ExtractStringList(attrs, "grantable_by") : existing.GrantableBy`
  (the omit→keep / present→replace pattern established for the permission fields).
- **Gate:** add `"grantable_by"` to the `setsRolePerms` trigger in `EnforcePrivilegeFloorAsync`
  so setting or changing it requires a global admin — the same gate that already protects
  `role.permissions`.

### Enforcement (floor, on user writes touching `roles`)
Replace the role half of the `disallowed` computation. New decision order:

1. `IsGlobalAdminAsync(actor)` ⇒ allow anything. *(unchanged)*
2. For **each role `R` present in the request's `roles` list** (the managed path treats `roles`
   as a full replacement set, not a delta — so evaluation covers every role in the payload,
   matching today's behavior):
   - allow iff `R.GrantableBy ∩ actor.Roles ≠ ∅` (ordinal);
   - else reject.

Resolve the grantee roles with a single batched `GetRolesAsync(rolesInRequest)` (the method
already exists) rather than one `GetRoleAsync` per role, so the check costs one extra query
regardless of list size, and only for non-global-admins.

The "actor already holds `R`" allow-branch is removed. `actor.Roles` is still loaded — now
used only to match against `grantable_by`. Groups keep their existing own-held check.

Error: reuse the existing rejection, reworded to reflect delegation, e.g.
`"not permitted to assign role: <R>"`.

### Data flow (worked example)
1. A global admin sets `content_editor.grantable_by = ["user_manager"]` via managed role update.
2. A `user_manager` (holds the `user_manager` role) creates a user with
   `roles: ["content_editor"]` via `/managed/request`.
3. Floor: not a global admin → load `content_editor` →
   `{user_manager} ∩ {user_manager} ≠ ∅` → allow.

### Error handling / edge cases
- **Nonexistent role** in `attrs` ⇒ absent from the `GetRolesAsync` result ⇒ treated as empty
  `grantable_by` ⇒ reject (uniqueness/validation also catches it independently).
- **`grantable_by` null/empty** ⇒ reject for non-admins (the safe default).
- **Self-assignment** is permitted (the floor doesn't distinguish target user) — intended under
  explicit admin delegation.

### Backward compatibility & rollout
This is a **deliberate tightening**, not an additive change:
- Migration backfills `grantable_by = null` on all existing roles, so a non-admin who could
  previously assign roles they hold can now assign **nothing** until an admin populates
  `grantable_by`. Operators must configure `grantable_by` for the roles they want delegated.
- **Super admins are unaffected** (the `IsGlobalAdminAsync` bypass is unchanged) — the one hard
  backward-compat requirement.
- Existing tests asserting "a non-admin may assign a role they hold" must be updated to expect
  rejection (or to set `grantable_by` first).

### Testing
- **Floor:** a `user_manager` holder can assign `content_editor` when it is in `grantable_by`,
  and cannot when it is not; a super-admin assigns an arbitrary role with no `grantable_by`
  entry; a role the actor *holds* is no longer auto-grantable without a `grantable_by` entry.
- **Setter gating:** a non-global-admin setting `grantable_by` is rejected.
- **Round-trip:** `grantable_by` persists through `UpsertRoleAsync`/`HydrateRole`.
- **Scope regression guards:** `/user/create` with a configured default role still succeeds
  (unaffected by the floor change); the managed path remains gated.

Use the existing `dmart.Tests/Integration/RolePermissionRequestTests.cs` /
`PermissionServiceIntegrationTests.cs` harness (`BuildPerm`/`UpsertRoleAsync`,
`AuthedClient`/`CreateRecord`/`PostRequest`).

### Out of scope (YAGNI)
- A `grantable_by` analog for **groups** (symmetric follow-up if needed).
- `grantable_by` referencing **permissions** as grantors (named-role only here).
- Any change to `/user/create` or OAuth (verified unaffected above).

## Files touched
- `Dmart.Models/Core/Role.cs` — `GrantableBy` field.
- `DataAdapters/Sql/SqlSchema.cs` — CREATE column + migration.
- `DataAdapters/Sql/ExpectedColumnPatcher.cs` — expected column.
- `DataAdapters/Sql/AccessRepository.cs` — `SelectRoleColumns`, `HydrateRole`, `UpsertRoleAsync`.
- `Api/Managed/RequestHandler.cs` — role create/update read `grantable_by`; floor gates its set
  and enforces `grantable_by` on user writes (own-roles branch removed).
- `dmart.Tests/Integration/RolePermissionRequestTests.cs` — floor + round-trip + gating tests.
