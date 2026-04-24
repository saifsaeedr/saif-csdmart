# Glossary

Project-specific terms that show up in code, docs, and configuration. This is
not a lookup for generic industry acronyms (JWT, OTP, SSE, CSV, ...) — go to
Wikipedia for those. Entries for ACL, AOT, and JSON Schema appear here
because DMART has a specific shape or usage for each, not as a generic
definition.

Alphabetized. Each entry points to the authoritative source file (class or
record name; no line numbers — they rot).

---

## ACL entry

A row in an entry's access control list. Grants or denies access for a
specific user or role, optionally scoped to a subset of actions. Defined as
`record AclEntry` in `Dmart.Models/Core/Acl.cs`. Distinct from the broader
role-based permissions held in the Postgres `access` table.

## AOT / Native AOT

Ahead-of-Time compilation — the binary is native code, not IL interpreted at
runtime. csdmart ships as an AOT binary, which imposes real constraints (no
runtime reflection fallback, no dynamic code generation). `PublishAot=true`
in `dmart.csproj` drives this. See `ARCHITECTURE.md` for what it means for
contributors.

## Attachment

A secondary payload associated with an Entry. Unlike the entry's primary
payload, attachments are stored separately and referenced by the entry.
Shaped as `record Attachment` in `Dmart.Models/Core/Attachment.cs`;
persisted in the `attachments` table and served by `AttachmentRepository`.

## AuthzCache

Two PostgreSQL materialized views (`mv_user_roles`, `mv_role_permissions`)
that pre-compute the user → role → permission join graph so the in-process
`PermissionService` doesn't walk JSONB on every request. Refreshed after
any write to users/roles/permissions, and on boot. See
`DataAdapters/Sql/AuthzCacheRefresher.cs`.

## Catalog

The second embedded Svelte SPA, alongside CXB. Served at `/cat/` by default
(configurable via `CAT_URL`). Ships inside the AOT binary via
`ManifestEmbeddedFileProvider`. See `Middleware/CatalogMiddleware.cs` and
the `catalog/` source tree.

## CXB

**C**ustomer e**X**perience **B**uilder — the primary admin Svelte SPA,
embedded in the dmart binary and served at `/cxb/` by default (configurable
via `CXB_URL`). Same embedding mechanism as Catalog. See
`Middleware/CxbMiddleware.cs`, the `cxb/` source tree, and `cxb/README.md`
for the title expansion.

## DMART

The project this code implements — a headless information-management
system for structured data with entries, schemas, workflows, and
permissions. "dmart" (lowercase, Python) is the original; "csdmart"
(this repo) is the C# port. When docs say "DMART" without prefix, context
determines which; on the wire and in the binary itself the two are kept
in parity. Entry point: `Program.cs`.

## Entry

The fundamental unit of data in DMART. A coherent piece of information
identified by the tuple `(space, subpath, shortname, resource_type)`,
optionally with a payload (typed by a schema) and attachments. Everything
else in the system is either an entry or metadata about entries. Defined
in `Dmart.Models/Core/Entry.cs`.

## Hook plugin

A plugin that runs in response to entry lifecycle events (create / update /
delete), either before or after the event. Registered via the `IHookPlugin`
interface. Distinct from API plugins (which expose HTTP endpoints) and
native plugins (which are `.so` files). See `Plugins/BuiltIn/` for the
in-process implementations.

## JSON Schema

The validation format used to constrain entry payloads. Entries of type
`content` reference a `schema` entry in the same space; payloads are
validated against it by `Services/SchemaValidator.cs` on write. Uses the
`JsonSchema.Net` library (Draft 7).

## Locator

The 4-tuple `(ResourceType, SpaceName, Subpath, Shortname)` that uniquely
addresses any entry — without its payload or attachments. Passed around as a
compact parameter type by every service/repository method that needs to
load or mutate a single entry. Defined in `Dmart.Models/Core/Locator.cs`.

## Managed endpoints

Authenticated API endpoints under `/managed/*`. Require a valid JWT. The
primary CRUD and query surface for logged-in users. See `Api/Managed/`.

## Management space

The special space named `management` that holds users, roles, permissions,
and the meta-schemas. Every DMART instance has exactly one; it is
bootstrapped on first boot by `DataAdapters/Sql/AdminBootstrap.cs`.
`DmartSettings.ManagementSpace` allows renaming, though Python parity
expects the default.

## maqola

A sample/demo space name (Arabic مقولة = "saying" / "quotation"). Appears
in i18n strings and fixture data. Not a feature — just a namespace used in
examples. *(Context.)*

## MCP (Model Context Protocol)

The JSON-RPC-over-HTTP/SSE dialect that AI assistants (Claude Desktop, Zed,
Cursor) use to invoke server-side tools. csdmart exposes its queries and
mutations as MCP tools for in-IDE use. See `Api/Mcp/McpEndpoint.cs`,
`Api/Mcp/McpRegistry.cs`, and `docs/plugins-and-mcp.md`.

## Native plugin

A separately-compiled shared library (`.so` on Linux) that the dmart binary
loads at startup via `dlopen` + function-pointer lookup. Can be written in
any language that produces a C ABI shared library (C#, Rust, C have working
examples). Contrast with built-in hook plugins, which are compiled into the
server binary. See `Plugins/Native/NativePluginLoader.cs` and
`custom_plugins_sdk/`.

## Payload

The optional content attached to an Entry — distinct from its metadata.
Can be JSON (`content_type: json` with an inline `body` dict validated
against the entry's schema), or a reference to a file for media types.
Defined in `Dmart.Models/Core/Payload.cs`. See also `docs/data-model.md`.

## Progress-ticket

The HTTP action that transitions a ticket entry through its workflow state
machine. Endpoint: `PUT /managed/progress-ticket/{space}/{subpath}/{shortname}/{action}`.
Handled by `Api/Managed/ProgressTicketHandler.cs`; state logic in
`Services/WorkflowEngine.cs`.

## Public endpoints

Unauthenticated API endpoints under `/public/*`. Limited to query and
submit operations, with access controlled by entry-level ACLs that permit
anonymous access. See `Api/Public/`.

## Query policies

A Postgres `TEXT[]` column on every ACL-filterable table (`entries`,
`users`, `roles`, `permissions`, `spaces`) holding the policy strings that
determine which callers can see each row. Populated at write time from the
row's owner, subpath, and resource type by `Utils/QueryPolicies.cs`; read
at query time by `AppendAclFilter` in `DataAdapters/Sql/QueryHelper.cs`
via a LIKE-intersection against the caller's resolved policy list. A row
with an empty array is invisible to everyone. The `fix_query_policies` CLI
subcommand back-fills legacy rows.

## Record

The wire-format envelope used in REST request and response bodies. Wraps an
Entry in a flatter `attributes` shape and optionally embeds attachments
grouped by resource type — matches what Python dmart emits. Defined as
`record Record` in `Dmart.Models/Api/Request.cs`.

## Reporter

Optional provenance on an Entry: who/what reported it, via which channel
(web/mobile/bot), through which distributor, and from which governorate.
Present on every Entry but semantically most meaningful on Tickets, where
it identifies an external reporter. Defined in `Dmart.Models/Core/Reporter.cs`.

## Resource type

The classification field (`resource_type`) that says what kind of thing an
Entry is: `content`, `folder`, `user`, `role`, `permission`, `schema`,
`ticket`, `comment`, `reply`, `reaction`, `media`, etc. Determines which
table stores the row and which validation rules apply. One of 31 enum
values. Defined in `Dmart.Models/Enums/ResourceType.cs`.

## Shortname

The short identifier of an entry within its `(space, subpath)` scope —
like a filename in a folder. Unique within that scope, not globally.
Python dmart pins the pattern at `^[a-zA-Zء-ي0-9٠-٩_]{1,64}$` (ASCII +
Arabic letters, ASCII + Arabic-Indic digits, underscore); csdmart mirrors
that shape via DB constraint and caller-level checks rather than a single
shared regex. Combined with `space_name`, `subpath`, and `resource_type`
it yields the `Locator` — the authoritative primary key across dmart tables.

## Space

The top-level organizational unit — a "tenant" or "realm" boundary. Every
Entry belongs to exactly one space. Standard spaces include `management`,
`applications`, `archive`, `personal`; user spaces are created via
`POST /managed/space`. Defined in `Dmart.Models/Core/Space.cs`.

## Subpath

The hierarchical folder-like path inside a Space where an Entry lives
(e.g. `/users/employees`, `/`). Always starts with `/` in DB storage, but
the wire format on `Record.subpath` strips the leading slash for the
user-facing envelope (matches Python dmart's `Record.__init__`). Logical
address only — not a filesystem path. Normalization lives in
`Dmart.Models/Core/Locator.cs::NormalizeSubpath`.

## Ticket

A special Entry kind that carries workflow state: current state,
resolution, assignee, comment history. Movement between states happens via
`PUT /managed/progress-ticket/…`. The valid state graph is defined by the
referenced Workflow. `resource_type = ticket` (see
`Dmart.Models/Enums/ResourceType.cs`).

## Translation

Internationalized text fields on certain entries — per-language variants
of display names and descriptions. Defined in
`Dmart.Models/Core/Translation.cs`.

## Workflow

A schema-defined state machine that governs Ticket transitions — which
states exist, which transitions are allowed, what resolution values each
terminal state requires. Stored as a regular `content` Entry whose payload
conforms to the `workflow_definition` schema. Evaluated by
`Services/WorkflowEngine.cs`.

## ZainMart

A commercial deployment of DMART (Zain telecom's marketplace). Appears in
`curl.sh` examples, issue reports, and some test fixtures — not a DMART
feature. *(Context: an external user/instance.)*
