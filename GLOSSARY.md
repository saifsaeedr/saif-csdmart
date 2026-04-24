# Glossary

Project-specific terms that show up in code, docs, and configuration. Industry-standard acronyms (ACL, JWT, OTP, AOT, SSE, CSV) are not repeated here — follow their normal references.

Each entry points to the authoritative source file. Line numbers are deliberately omitted since they rot; use the class/record/namespace name to anchor.

---

## AuthzCache

Two PostgreSQL materialized views (`mv_user_roles`, `mv_role_permissions`) that pre-compute the user → role → permission join graph so the in-process `PermissionService` doesn't have to walk JSONB at every request. The views are refreshed after any write to users/roles/permissions, and on boot. See `DataAdapters/Sql/AuthzCacheRefresher.cs`.

## Catalog

The second embedded Svelte SPA, alongside CXB. Served at `/cat/` by default (configurable via `CAT_URL`). Ships inside the AOT binary via `ManifestEmbeddedFileProvider`. See `Middleware/CatalogMiddleware.cs` and the `catalog/` source tree.

## CXB

**C**ustomer e**X**perience **B**uilder — the primary admin Svelte SPA, embedded in the dmart binary and served at `/cxb/` by default (configurable via `CXB_URL`). Same embedding mechanism as Catalog. See `Middleware/CxbMiddleware.cs`, the `cxb/` source tree, and the `cxb/README.md` header.

## DMART

The platform this repository implements — a structure-oriented information-management layer (data-as-a-service) delivered as a single ~37 MB AOT-native binary on .NET 10 + PostgreSQL + Svelte. "DMART" is the product name used in docs and CLI. Entry point at `Program.cs`.

## Entry

The fundamental unit of data in DMART. A coherent piece of information (metadata + optional payload) uniquely identified by (`space_name`, `subpath`, `shortname`, `resource_type`). Mirrors dmart Python's `Entries` SQLModel table. Defined in `Dmart.Models/Core/Entry.cs`.

## Locator

The 4-tuple `(ResourceType, SpaceName, Subpath, Shortname)` that uniquely addresses any entry. Passed around as a compact parameter type by every service/repository method that needs to load or mutate a single entry. Defined in `Dmart.Models/Core/Locator.cs`.

## Management space

The special space named `management` that holds users, roles, permissions, and the meta-schemas. Every DMART instance has exactly one; it is bootstrapped on first boot by `DataAdapters/Sql/AdminBootstrap.cs`. `DmartSettings.ManagementSpace` allows renaming, but Python parity expects `"management"`.

## maqola

A sample/demo space name (Arabic مقولة = "saying" / "quotation"). Appears in some i18n strings and fixture data. Not a feature — just a namespace used in examples. *(Context.)*

## MCP

Model Context Protocol — the JSON-RPC-over-HTTP/SSE dialect that AI assistants (Claude Desktop, Zed, Cursor) use to invoke server-side tools. DMART exposes its queries/mutations as MCP tools for in-IDE use. See `Api/Mcp/McpEndpoint.cs`, `Api/Mcp/McpRegistry.cs`, and `docs/plugins-and-mcp.md`.

## Native plugin

A separately-compiled C# shared library (`.so` on Linux) that the dmart binary loads at startup via `dlopen` + function-pointer lookup. Used for hot-path hooks where the in-process C# hook plugins aren't fast enough. Contract and SDK live under `custom_plugins_sdk/` and `Plugins/Native/NativePluginLoader.cs`.

## Payload

The optional content attached to an Entry — distinct from the Entry's metadata. Can be JSON (`content_type: json` with an inline `body` dict) or bytes (media, handled via attachments). Defined in `Dmart.Models/Core/Payload.cs`. See also `docs/data-model.md`.

## Query policy

An access-control marker stored as a dotted string in the `query_policies TEXT[]` column on every entry-carrying row. The SQL-level row filter (`AppendAclFilter` in `DataAdapters/Sql/QueryHelper.cs`) intersects a caller's policy list against each row's array; a row with an empty array is invisible to everyone. Managed by `fix_query_policies` CLI subcommand when back-filling legacy rows.

## Record

The wire-format envelope used in REST request and response bodies. Wraps an Entry with a flatter `attributes` shape and optional embedded `attachments` grouped by resource type, matching what Python dmart emits. Defined in `Dmart.Models/Api/Record.cs`.

## Reporter

Optional provenance on a Ticket entry — who/what reported it, via which channel (web/mobile/bot), through which distributor. Mirrors Python's `core.Reporter`. Defined in `Dmart.Models/Core/Reporter.cs`.

## Resource type

The classification field (`resource_type`) that says what kind of Entry you're looking at: `content`, `folder`, `user`, `role`, `permission`, `schema`, `ticket`, `comment`, `reply`, `reaction`, `media`, and ~20 more. One of 31 enum values. Defined in `Dmart.Models/Enums/ResourceType.cs`.

## Shortname

The short identifier of an entry within its `(space, subpath)`. Python dmart pins the pattern at `^[a-zA-Zء-ي0-9٠-٩_]{1,64}$` (ASCII + Arabic letters, ASCII + Arabic-Indic digits, underscore); the C# port mirrors that shape but enforces it at the DB-constraint and caller levels rather than via a single shared regex. Combined with `space_name`, `subpath`, and `resource_type` it yields the `Locator` — the authoritative primary key across all dmart tables. See `Dmart.Models/Core/Locator.cs`.

## Space

The top-level container — a "tenant" or "realm" boundary. Every Entry belongs to exactly one space. Standard spaces include `management`, `applications`, `archive`, `personal`; user spaces are created via `POST /managed/space`. Defined in `Dmart.Models/Core/Space.cs`.

## Subpath

The hierarchical folder-like path inside a Space where an Entry lives (e.g. `/users/employees`, `/`). Always starts with `/` in DB storage, but the wire format on `Record.subpath` strips the leading slash for the user-facing envelope (matches Python dmart's `Record.__init__`). Logical address only — not a filesystem path. Normalization lives in `Dmart.Models/Core/Locator.cs::NormalizeSubpath`.

## Ticket

A special Entry kind that carries workflow state: current state, resolution, assignee, comment history. Movement between states happens via `PUT /managed/progress-ticket/...`. The valid state graph is defined by the referenced Workflow schema. `resource_type = ticket` (see `Dmart.Models/Enums/ResourceType.cs`).

## Workflow

A JSON-schema-defined state machine that governs Ticket transitions — which states exist, which transitions are allowed, what resolution values each terminal state requires. Stored as a regular `content` Entry whose payload conforms to the `workflow_definition` schema. Evaluated by `Services/WorkflowEngine.cs`.

## ZainMart

A commercial deployment of DMART (Zain telecom's marketplace). Appears in curl.sh examples, issue reports, and some test fixtures — not a DMART feature. *(Context: an external user/instance.)*
