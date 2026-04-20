# Data model

The C# port runs against the same PostgreSQL schema as dmart Python. This
document describes (a) the wire shapes clients see, (b) the tables, (c) the
non-obvious rules that keep the two in sync.

## Wire envelope

Every HTTP response uses this shape:

```json
{
  "status": "success" | "failed",
  "error":  { "type": "...", "code": <int>, "message": "...", "info": [ {...} ] } | null,
  "records": [ Record, ... ] | null,
  "attributes": { "total": <int>, "returned": <int>, ... } | null
}
```

- `status` is a string, not an int — Python's `StrEnum`.
- `error` is omitted entirely on success (not `"error": null`).
- `records[]` on list endpoints; on single-entry GETs the record shape is
  spread at the top level.

A `Record` looks like:

```json
{
  "resource_type": "content",
  "uuid": "...",
  "shortname": "myentry",
  "subpath": "api/v1",             // stripped — no leading slash on the wire
  "attributes": { ... }
}
```

`InternalErrorCode` (`Models/Api/InternalErrorCode.cs`) defines the integer
codes. Selected values:

| Code | Constant | Typical type |
|---|---|---|
| 10 | `INVALID_USERNAME_AND_PASS` | auth |
| 11 | `USER_ISNT_VERIFIED` | auth |
| 17 | `INVALID_PASSWORD_RULES` | jwtauth |
| 47 | `INVALID_TOKEN` | jwtauth |
| 48 | `EXPIRED_TOKEN` | jwtauth |
| 49 | `NOT_AUTHENTICATED` | jwtauth |
| 110 | `USER_ACCOUNT_LOCKED` | auth |
| 125 | `INVALID_INVITATION` | jwtauth |
| 204 | `CANNT_DELETE` | request |
| 220 | `OBJECT_NOT_FOUND` | db / request |
| 230 | `INVALID_ROUTE` | request |
| 400 | `SHORTNAME_ALREADY_EXIST` | db |
| 404 | `SHORTNAME_DOES_NOT_EXIST` | db |
| 409 | `CONFLICT` | db |
| 430 | `SOMETHING_WRONG` | request |

`Api/FailedResponseFilter.cs` maps a subset to HTTP status codes (401 for
auth, 404 for not-found, 409 for conflicts, 423 for locks, 403 for OTP resend
blocked; everything else → 400).

## Subpath rules

The slash dance is the #1 confusion source.

| Context | Form | Example |
|---|---|---|
| Wire (`Record.subpath`) | stripped | `"api/v1"` |
| Wire (`Query.subpath`) | either — normalized on set | `"api/v1"` or `"/api/v1"` |
| Storage (`entries.subpath`, `attachments.subpath`) | leading slash | `"/api/v1"` |
| Root | `"/"` in both | `"/"` |
| Permission row `subpaths` value | **slash-free after Python's `trans_magic_words` normalization** — but Python writers sometimes store with a leading slash. Our matcher normalizes at compare time. | `"denominations"` OR `"/denominations"` |

See `Models/Core/Locator.NormalizeSubpath(string)` — adds the leading slash
unless the input is empty or already `"/"`.

`Services/PermissionService.NormalizePermissionSubpath(string)` — Python's
`data_adapters/helpers.py::trans_magic_words` tail: collapses `//`, strips
leading/trailing slash (unless whole pattern is `/`). This is the one that
rescues mismatched-slash permission rows.

## Entity-relationship

```mermaid
erDiagram
    users ||--o{ sessions : "created by"
    users ||--o{ invitations : "targets"
    users ||--o{ entries : "owner_shortname FK"
    users ||--o{ attachments : "owner_shortname FK"
    users ||--o{ permissions : "owner_shortname FK"
    users ||--o{ roles : "owner_shortname FK"
    roles  }|..|{ users : "users.roles JSONB[]"
    permissions }|..|{ roles : "roles.permissions JSONB[]"
    mv_user_roles }o--|| users : "flattens"
    mv_role_permissions }o--|| roles : "flattens"
    entries ||--o{ attachments : "parent_subpath+parent_shortname"
    entries ||--o{ histories : "space+subpath+shortname"
    entries ||--o{ locks : "space+subpath+shortname"
    spaces ||--o{ entries : "space_name"
    spaces ||--o{ attachments : "space_name"
    otp }o..o{ users : "keyed by msisdn/email/shortname"
    count_history }o--|| spaces : "snapshots"
    urlshorts }o..o{ entries : "short codes"
```

## Tables (what lives where)

### `entries`

Everything that isn't a User, Role, Permission, Space, or attachment-type.
Content, Folder, Schema, Ticket, DataAsset, Alteration (those specific five
are stored **as** attachments — see below), and a few more.

Key columns:
- `uuid`, `shortname`, `space_name`, `subpath` (leading slash), `resource_type`
- `is_active`, `slug`, `displayname` (jsonb), `description` (jsonb), `tags` (jsonb)
- `payload` (jsonb) — contains `schema_shortname`, `content_type`, `body`
- `owner_shortname` (FK → users), `owner_group_shortname`, `acl` (jsonb array of `{user_shortname, allowed_actions}`)
- `relationships`, `last_checksum_history`
- `state`, `is_open`, `reporter` (jsonb), `workflow_shortname`, `collaborators`, `resolution_reason` — ticket fields
- `query_policies` (text[]) — precomputed authz LIKE patterns
- `embedding` (vector) — optional, for semantic search

Repo: `DataAdapters/Sql/EntryRepository.cs`. Mapper: `Services/EntryMapper.cs`.

### `attachments`

Specific resource types whose bytes + JSON coexist and whose parent is an
entry: `Comment`, `Reply`, `Reaction`, `Media`, `Json`, `Share`, `Lock`
(as attachment, not the `locks` table), `DataAsset`, `Relationship`,
`Alteration`.

Key columns:
- Same Metas base as `entries`
- `media` (BYTEA) — raw bytes for Media/etc.
- `body` (TEXT) — small inline text
- `parent_subpath`, `parent_shortname`, `parent_type` — binds to an entry

Repo: `AttachmentRepository.cs`.

### `users`

Management-space entries keyed by `shortname`.

Key columns:
- Metas base + `email` (ci-unique), `msisdn`, `password` (Argon2id PHC hash)
- `type` (usertype enum: `web`/`mobile`/`bot`), `language` (language enum: `ar`/`en`/`ku`/`fr`/`tr`)
- `roles` (jsonb text[]), `groups` (jsonb text[])
- `is_email_verified`, `is_msisdn_verified`, `force_password_change`
- `locked_to_device`, `device_id`, `attempt_count`, `last_login` (jsonb)
- `notes` (jsonb)
- `query_policies` (text[])

Repo: `UserRepository.cs`. Note: **two** enum mappings per `Language` and
`UserType` — the PG column stores `ar`/`en`/`ku`/`fr`/`tr` (ISO codes ← C#
member names lowercased), while the wire JSON carries `arabic`/`english`/…
(← `[EnumMember]` values). See `JsonbHelpers.EnumMember<T>` vs
`JsonbHelpers.EnumNameLower<T>`.

### `roles`

Key columns: Metas base + `permissions` (jsonb text[]).

### `permissions`

Key columns:
- Metas base
- `subpaths` — jsonb dict `{"space_name": ["subpath", ...]}`; values support
  magic words `__all_subpaths__`, and keys support `__all_spaces__`.
- `resource_types` — jsonb text[]
- `actions` — jsonb text[] (`view`, `query`, `create`, `update`, `delete`, …)
- `conditions` — jsonb text[] (`is_active`, `own`)
- `restricted_fields` — jsonb (`null` means no restriction)
- `allowed_fields_values` — jsonb

Repo: `AccessRepository.cs` (handles both roles and permissions).

### `spaces`

One row per space. The `management` space holds users/roles/permissions/
folders-for-them.

Key columns: Metas base + Space-specific (`root_registration_signature`,
`primary_website`, `indexing_enabled`, `capture_misses`, `check_health`,
`languages` (jsonb Language array), `icon`, `mirrors`, `hide_folders`,
`hide_space`, `active_plugins`, `ordinal`).

**Unique constraint:** `(shortname, space_name, subpath)` — NOT just
`shortname`. `SpaceRepository.UpsertAsync` uses that tuple on conflict.

### `sessions`

One row per (user, access token) pair. Columns: `shortname`, `access_token`
(stored verbatim for `session_inactivity_ttl` checks), `last_used_at`,
`firebase_token`. Used by JwtBearerSetup for session-based inactivity
enforcement.

### `invitations`

Single-use JWT-backed invitations. Columns: `shortname`, `invitation`
(the JWT), `expires_at`. See [auth.md](./auth.md).

### `histories`

Change log. Every create/update/delete writes a row with `event_shortname`,
`diff`, `request_headers`.

### `locks`, `otp`, `urlshorts`, `count_history`

Straightforward single-purpose tables. See their repos for columns.

### Materialized views

- `mv_user_roles` — `(user_shortname, role_shortname)` flattened from `users.roles`
- `mv_role_permissions` — `(role_shortname, permission_shortname)` flattened from `roles.permissions`

Both have UNIQUE indexes so `REFRESH MATERIALIZED VIEW CONCURRENTLY` works.
`AuthzCacheRefresher.RefreshAsync` runs them after every user/role/permission
write and at boot.

## Non-obvious storage rules (discovered the hard way)

These are documented here so you don't relitigate them.

### `JsonStringEnumConverter` ignores `[EnumMember]` under source-gen

Don't set `UseStringEnumConverter` on `JsonSourceGenerationOptions`. Use the
custom `EnumMemberConverterBase<T>` in `Models/Json/EnumMemberConverter.cs`
with one concrete subclass per enum. Each enum has `[JsonConverter(typeof(...))]`.

### Two mappings for `Language` and `UserType`

- Wire: `arabic`, `english`, `kurdish`, `french`, `turkish` (`[EnumMember]`)
- DB enum column: `ar`, `en`, `ku`, `fr`, `tr` (lowercased C# member name)

`UserRepository` casts via `$22::usertype` and `$23::language` in the SQL.

### NOT NULL JSONB columns default to empty

`tags`, `roles`, `groups`, `permissions`, `subpaths`, `resource_types`,
`actions`, `conditions`, `languages` all reject NULL. `JsonbHelpers.ToJsonbList(null)`
returns `"[]"` and `ToJsonbDict(null)` returns `"{}"`.

### `filter_schema_names=["meta"]` is a sentinel, not a filter

Python's `Query.filter_schema_names` defaults to `["meta"]` (Pydantic default).
`QueryHelper.BuildWhereClause` strips the literal `"meta"` and only emits a
SQL filter for the remainder. Internal callers that want all schemas must
pass `FilterSchemaNames = new()` (empty list).

### `source-gen JSON` misbehaves with `object` values

A `Dictionary<string, object>` sent through source-gen fails when a value's
runtime type isn't registered. `long` fails (Postgres `COUNT(*)` returns it)
— cast to `int` before adding. `string[]` fails — use `List<string>`.
`JsonElement` needs to be registered explicitly. Polymorphic `object` is
the hardest AOT path.

### Source-gen JSON ignores C# property initializers

`public bool RetrieveTotal { get; init; } = true;` silently flips to
`false` when the incoming JSON omits the key. Any field with a non-`default(T)`
initializer must be nullable (`bool?`), with the consumer treating `null` as
the intended default.

### `DefaultIgnoreCondition` in the source-gen attribute doesn't carry over

`[JsonSourceGenerationOptions(DefaultIgnoreCondition = WhenWritingNull)]`
drives TypeInfo metadata, but not `SerializerOptions` at runtime. Must also
set `o.SerializerOptions.DefaultIgnoreCondition = WhenWritingNull` in
`ConfigureHttpJsonOptions` or nulls leak onto the wire.

### Timestamp Kind matters

Postgres `timestamp without time zone` + Npgsql 6+ rejects `DateTime.UtcNow`
(Kind=Utc). **First line of `Program.cs`:**
`AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`.

### Resource-type → repository dispatch

See `RequestHandler.cs:Dispatch{Create,Update,Delete}Async`:

| `ResourceType` | Table / repo |
|---|---|
| `User` | `users` / UserRepository |
| `Role` | `roles` / AccessRepository.Upsert/DeleteRoleAsync |
| `Permission` | `permissions` / AccessRepository.Upsert/DeletePermissionAsync |
| `Space` | `spaces` / SpaceRepository |
| `Comment`, `Reply`, `Reaction`, `Media`, `Json`, `Share`, `Lock`, `DataAsset`, `Relationship`, `Alteration` | `attachments` / AttachmentRepository |
| everything else (`Content`, `Folder`, `Schema`, `Ticket`, `PluginWrapper`, …) | `entries` / EntryService → EntryRepository |

Get this wrong and you'll silently no-op — Role/Permission updates used
to fall through to `EntryService` and do nothing until commit 8cbb999.

## Where to go next

- How queries filter these tables → [query.md](./query.md)
- How permissions gate them → [permissions.md](./permissions.md)
