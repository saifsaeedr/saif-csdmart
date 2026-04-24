# DMART — Unified Data Platform (C# Port)

A fast, AOT-native headless information-management backend on .NET 10,
PostgreSQL, and Svelte. Ships as a single ~37 MB self-contained binary.

## The problem DMART solves

Valuable information — organizational and personal — tends to sprawl:

- Dispersed across too many systems, each with its own access context.
- Hard to consolidate, link, and reason about across silos.
- Locked to vendors or application-specific formats.
- Chaotic to discover and search as data piles up.
- Hard to master, dedup, back up, archive, and restore.
- Hard to protect, audit, and secure consistently.

DMART is a structure-oriented information-management layer (aka
Data-as-a-Service) that lets you treat information as a first-class asset:
authored cleanly, searched coherently, shared safely, and extended without
vendor lock-in. It targets small-to-medium footprints (up to ~300 million
primary entries) and is deliberately not aimed at workloads that need heavy
relational modeling or large multi-statement transactions.

## What is DMART?

A headless, low-code information inventory platform that assimilates
structured, unstructured, and binary data under a single REST-like JSON
API. Top highlights:

- **Data-as-a-Service backbone** — data assets are declared in logical
  business shapes and reused across applications and microservices
  without each one redefining its own schema.
- **Standardized JSON API** — a unified public API for every resource
  type; full OpenAPI 3 spec at `/docs`.
- **Entry-oriented data model** — a coherent information unit (meta +
  payload + attachments + relationships) lives as one logical entry,
  organized in hierarchical folders within spaces.
- **Schema validation** — JSON Schema enforcement on content payloads,
  referenced from a central `schema` subpath inside each space.
- **Built-in access control** — role-based permissions with per-entry
  ACLs, hierarchical subpath walks, precomputed `query_policies`
  filtering at the SQL level, and magic-word scope widening
  (`__all_spaces__`, `__all_subpaths__`).
- **Workflow engine** — configurable ticket state machines with lock,
  assign, and progress-transition endpoints.
- **Plugin system** — built-in hooks + external native `.so` plugins
  loaded at runtime + MCP tool surface for AI agents.
- **WebSocket** — real-time notifications via channel subscriptions.
- **Microservice-friendly** — JWT shared secret lets other services
  accept a dmart session out of the box.
- **Admin UI** — CXB and Catalog Svelte SPAs embedded in the binary,
  served at `/cxb/` and `/cat/` with runtime-rewritten config so the
  same bundle works behind any reverse proxy.

New to DMART? Read [`GLOSSARY.md`](./GLOSSARY.md) for the project's
vocabulary. Contributing code? Read [`ARCHITECTURE.md`](./ARCHITECTURE.md)
first — it explains the constraints and the reasoning behind the unusual
choices.

## Quick Start

### Container (fastest — no build needed)

```
podman run --name dmart -p 8000:8000 -d -it ghcr.io/edraj/csdmart:latest
podman exec -it dmart dmart set_password
# Open http://localhost:8000/cxb/ or http://localhost:8000/cat/
```

### RPM (Fedora / RHEL 9)

```
sudo dnf install ./dmart-*.rpm
sudo vi /etc/dmart/config.env          # set DATABASE_PASSWORD, JWT_SECRET
dmart set_password                     # set admin password
sudo systemctl enable --now dmart
```

Download the RPM from the [latest release](https://github.com/edraj/csdmart/releases).

### From source

```
git clone https://github.com/edraj/csdmart
cd csdmart
cp config.env.sample config.env
vi config.env                          # set database credentials
dotnet run -- serve
# In another terminal:
dmart set_password
```

## CLI

```
dmart                      Show help
dmart serve                Start HTTP server
dmart version              Version and build info
dmart settings             Show effective configuration
dmart set_password         Set user password interactively
dmart init                 Initialize ~/.dmart/ with config files
dmart migrate              Create/update Postgres schema (idempotent)
dmart check <space>        Run health checks
dmart export <space>       Export space to zip
dmart import <file.zip>    Import from zip
dmart fix_query_policies   Backfill empty query_policies columns
dmart cli                  Interactive REPL client
dmart cli c <space> "ls"   Single CLI command
dmart cli s script.txt     Batch script
```

Run `dmart <subcommand> --help` for per-command details where supported.

## Configuration

Configuration sources, in priority order (later wins):

1. `config.env` — checked at `$BACKEND_ENV`, `./config.env`, `~/.dmart/config.env`
2. Environment variables prefixed `Dmart__` (double underscore = nested)

Unknown keys in `config.env` cause startup to fail — this catches typos
like `DATABAE_HOST` vs `DATABASE_HOST`. See `config.env.sample` for the
complete list of valid keys.

Key settings:

```
DATABASE_HOST="localhost"
DATABASE_PORT=5432
DATABASE_USERNAME="dmart"
DATABASE_PASSWORD="yourpassword"
DATABASE_NAME="dmart"
JWT_SECRET="at-least-32-bytes-long"
LISTENING_HOST="0.0.0.0"
LISTENING_PORT=5099
ALLOWED_CORS_ORIGINS="http://localhost:3000"
CXB_URL="/cxb"
LOG_LEVEL="information"
LOG_FORMAT="json"
```

The admin user `dmart` is created passwordless on first startup. Set a
password with `dmart set_password` before exposing the server.

## API Endpoints

| Group     | Path                                                       | Auth  | Description                                |
|-----------|------------------------------------------------------------|-------|--------------------------------------------|
| Root      | `GET /`                                                    | No    | Server identifier                          |
| Docs      | `GET /docs`                                                | No    | Swagger UI                                 |
| Docs      | `GET /docs/openapi.json`                                   | No    | OpenAPI spec                               |
| Auth      | `POST /user/login`                                         | No    | Login (returns JWT + cookie)               |
| Auth      | `POST /user/logout`                                        | Yes   | Logout                                     |
| Auth      | `GET /user/profile`                                        | Yes   | User profile                               |
| Managed   | `POST /managed/request`                                    | Yes   | CRUD (create/update/delete/move)           |
| Managed   | `POST /managed/query`                                      | Yes   | Query entries                              |
| Managed   | `GET /managed/entry/{type}/{space}/{subpath}/{shortname}`  | Yes   | Get single entry                           |
| Managed   | `POST /managed/resource_with_payload`                      | Yes   | Upload with file                           |
| Managed   | `POST /managed/csv`                                        | Yes   | CSV export                                 |
| Managed   | `POST /managed/resources_from_csv/{type}/{space}/{subpath}/{schema}` | Yes | CSV import                          |
| Managed   | `PUT /managed/progress-ticket/{space}/{subpath}/{shortname}/{action}` | Yes | Workflow state transition           |
| Public    | `POST /public/query`                                       | No    | Public query                               |
| Public    | `POST /public/submit/{space}/{schema}/{subpath}`           | No    | Public submission                          |
| Info      | `GET /info/manifest`                                       | Yes   | Server manifest and plugins                |
| Info      | `GET /info/settings`                                       | Yes   | Effective settings                         |
| WebSocket | `GET /ws?token=<jwt>`                                      | Token | Real-time channel subscriptions            |

See [`ARCHITECTURE.md`](./ARCHITECTURE.md#request-lifecycle) for how
requests flow through the system.

## Client libraries

Official SDKs for talking to the dmart REST API:

| Language / runtime | Package | Install |
|---|---|---|
| Python | [`pydmart`](https://pypi.org/project/pydmart/) | `pip install pydmart` |
| Python | [`dmart`](https://pypi.org/project/dmart/) (core + CLI) | `pip install dmart` |
| TypeScript / JavaScript (Node, Deno, Bun, browsers) | [`@edraj/tsdmart`](https://www.npmjs.com/package/@edraj/tsdmart) | `npm install @edraj/tsdmart` |
| Dart / Flutter | [`dmart`](https://pub.dev/packages/dmart) | `flutter pub add dmart` |
| C# / .NET | [`Dmart.Client`](./dmart.Client/) (ships from this repo) | `dotnet add package Dmart.Client` |

MCP-capable AI agents (Zed, Claude Code, Cursor, …) can connect directly
to `/mcp` on any dmart instance — no SDK needed. See
[`docs/plugins-and-mcp.md`](./docs/plugins-and-mcp.md).

## Plugins

### Built-in plugins

Compiled into the binary. Configured via `plugins/<name>/config.json`:

- `resource_folders_creation` — auto-creates `/schema` folder on space creation
- `realtime_updates_notifier` — WebSocket broadcasts on CRUD events
- `audit` — logs all dispatched events
- `db_size_info` — API plugin at `GET /db_size_info/`

### External native plugins

Drop a `.so` + `config.json` into `~/.dmart/plugins/<name>/` — no
recompile needed:

```
mkdir -p ~/.dmart/plugins/my_plugin
cp my_plugin.so ~/.dmart/plugins/my_plugin/
cat > ~/.dmart/plugins/my_plugin/config.json << 'EOF'
{
  "shortname": "my_plugin",
  "is_active": true,
  "type": "hook",
  "listen_time": "after",
  "filters": {
    "subpaths": ["__ALL__"],
    "resource_types": ["content"],
    "schema_shortnames": ["__ALL__"],
    "actions": ["create", "update", "delete"]
  }
}
EOF
```

### Building custom plugins

```
cd custom_plugins_sdk/<name>
dotnet publish <name>.csproj -c Release -r linux-x64 -o /tmp/<name>-build
cp /tmp/<name>-build/<name>.so ~/.dmart/plugins/<name>/
```

Plugins export a C-ABI surface: `get_info()`, `hook()` or
`handle_request()`, `free_string()`. Can be written in any language that
produces a C ABI shared library (C#, Rust, C, Go). See
[`custom_plugins_sdk/README.md`](./custom_plugins_sdk/README.md) for the
full development guide with working examples.

## Building

```
# Development
dotnet run -- serve

# Production (AOT native binary)
./build.sh
# Output: bin/dmart (~35MB single binary)

# RPM packages
./dist/build-rpm.sh          # Fedora
./dist/build-rpm.sh el9      # RHEL 9 via podman
./dist/build-rpm.sh srpm     # Source RPM
```

## Testing

```
# Unit and integration tests
dotnet test dmart.Tests/dmart.Tests.csproj -c Release

# E2E smoke tests against a running server
DMART_URL=http://localhost:5099 ./curl.sh
```

See [`docs/testing.md`](./docs/testing.md) for fixtures, parallelism
rules, and the commands that include DB-backed integration tests.

## Documentation

Engineering reference for maintainers and contributors, with Mermaid
diagrams:

- [`GLOSSARY.md`](./GLOSSARY.md) — DMART-specific vocabulary (Entry, Space, CXB, MCP, …)
- [`ARCHITECTURE.md`](./ARCHITECTURE.md) — constraints, request lifecycle, directory guide
- [`docs/README.md`](./docs/README.md) — navigation for the rest of the docs tree
- [`docs/data-model.md`](./docs/data-model.md) — ER diagram, wire-format rules, repositories
- [`docs/permissions.md`](./docs/permissions.md) — the permission walk, anonymous + world, ACL, conditions
- [`docs/auth.md`](./docs/auth.md) — login / JWT / session flows, OAuth providers, invitations
- [`docs/plugins-and-mcp.md`](./docs/plugins-and-mcp.md) — plugin lifecycle + MCP protocol + OAuth discovery
- [`docs/query.md`](./docs/query.md) — query types, search syntax, sort_by, ACL filtering
- [`docs/testing.md`](./docs/testing.md) — xUnit + curl.sh + parallelism + common recipes
- [`docs/debugging.md`](./docs/debugging.md) — known pitfalls, AOT gotchas, SQL inspection
- [`docs/contributing.md`](./docs/contributing.md) — recipes: add endpoint, repository, service, plugin

## Deployment

### Systemd (RPM)

```
sudo dnf install ./dmart-*.rpm
sudo vi /etc/dmart/config.env
sudo systemctl enable --now dmart
journalctl -u dmart -f
```

### Docker (all-in-one)

```
./admin_scripts/docker/notes.sh
# Includes PostgreSQL 18 + dmart
# Access: http://localhost:8000/cxb/
```

## Project Layout

See [`ARCHITECTURE.md`](./ARCHITECTURE.md#directory-guide) for a complete
directory walkthrough. Briefly:

```
Api/              HTTP handlers (Minimal API)
Auth/             JWT, Argon2, OTP, OAuth
Cli/              Interactive CLI client
Config/           Settings and config.env parsing
DataAdapters/     Postgres repositories and schema
Middleware/       CORS, CXB, logging, headers
Models/           Domain types and API DTOs, plus DmartJsonContext
Plugins/          Built-in and native plugin loader
Services/         Business logic
```

## Technology

- **.NET 10** with Native AOT — single binary, no runtime needed
- **PostgreSQL** — DMART DDL in `DataAdapters/Sql/SqlSchema.cs`
- **Npgsql** — direct SQL, no ORM
- **Svelte** — CXB and Catalog admin UIs, embedded in the binary
- **System.Text.Json** with source-generated serializers

## License

AGPL-3.0
