# DMART — Unified Data Platform

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

DMART is a structure-oriented information management layer (aka
Data-as-a-Service) that lets you treat information as a first-class asset:
authored cleanly, searched coherently, shared safely, and extended
without vendor lock-in. It targets small-to-medium footprints (up to
~300 million primary entries) and is deliberately not aimed at
workloads that need heavily relational modeling or large multi-statement
transactions.

## What is DMART?

A headless, low-code information inventory platform that assimilates
structured, unstructured, and binary data under a single REST-like
JSON API. Top highlights:

- **Data-as-a-Service backbone** — data assets are declared in logical
  business shapes and reused across applications and microservices
  without each one redefining its own schema.
- **Standardized JSON API** — a unified public API for every resource
  type; full OpenAPI 3 spec at `/docs`.
- **Entry-oriented** — a coherent information unit (meta + payload +
  attachments + relationships) lives as one logical entry, not scattered
  rows. See [docs/data-model.md](./docs/data-model.md).
- **Schema validation** — JSON Schema enforcement on content payloads,
  referenced from a central `schema` subpath inside each space.
- **Built-in access control** — role-based permissions with per-entry
  ACLs, hierarchical subpath walks, precomputed `query_policies`
  filtering at the SQL level, and magic-word scope widening
  (`__all_spaces__`, `__all_subpaths__`). See
  [docs/permissions.md](./docs/permissions.md).
- **Workflows** — configurable ticket state machines with lock, assign,
  and progress-transition endpoints.
- **Microservice-friendly** — JWT shared secret lets other services
  accept a dmart session out of the box.
- **Plugin extensibility** — built-in hooks + native `.so` plugins
  discovered at runtime + MCP tool surface for AI agents. See
  [docs/plugins-and-mcp.md](./docs/plugins-and-mcp.md).
- **Real-time** — WebSocket channel subscriptions broadcast CRUD events.
- **Admin UI** — CXB Svelte SPA embedded in the binary, served at
  `/cxb/` with runtime-rewritten config so the same bundle works behind
  any reverse proxy.

## Quick Start

### Container (fastest — no build needed)

Pull the pre-built all-in-one image (dmart + PostgreSQL) from GitHub Container Registry:

```bash
podman run --name dmart -p 8000:8000 -d -it ghcr.io/edraj/csdmart:latest
```

Set the admin password and open the UI:

```bash
podman exec -it dmart dmart set_password
# Open http://localhost:8000/cxb/
```

### RPM (Fedora / RHEL 9)

Download the RPM from the [latest release](https://github.com/edraj/csdmart/releases):

```bash
sudo dnf install ./dmart-*.rpm
sudo vi /etc/dmart/config.env          # set DATABASE_PASSWORD, JWT_SECRET
dmart set_password                     # set admin password
sudo systemctl enable --now dmart
```

### From Source

```bash
git clone https://github.com/edraj/csdmart
cd csdmart
cp config.env.sample config.env
vi config.env                          # set database credentials
dotnet run -- serve
# In another terminal:
dmart set_password                     # set admin password
```

## CLI

```bash
dmart                     # show help
dmart serve               # start HTTP server
dmart -v                  # version info
dmart settings            # show effective configuration
dmart set_password        # set user password interactively
dmart init                # initialize ~/.dmart/ with config files
dmart check <space>       # run health checks
dmart export <space>      # export space to zip
dmart cli                 # interactive REPL client
dmart cli c <space> "ls"  # single command
dmart cli s script.txt    # batch script
```

## Configuration

Configuration sources (later wins):
1. `config.env` (`$BACKEND_ENV` or `./config.env` or `~/.dmart/config.env`)
2. Environment variables (`Dmart__Key`)

Key settings in `config.env`:

```ini
DATABASE_HOST="localhost"
DATABASE_PORT=5432
DATABASE_USERNAME="dmart"
DATABASE_PASSWORD="yourpassword"
DATABASE_NAME="dmart"
JWT_SECRET="your-secret-at-least-32-bytes-long"
LISTENING_HOST="0.0.0.0"
LISTENING_PORT=5099
ALLOWED_CORS_ORIGINS="http://localhost:3000"
CXB_URL="/cxb"
LOG_LEVEL="information"
LOG_FORMAT="json"
```

The admin user `dmart` is created passwordless on first startup. Set a password with `dmart set_password`.

## API Endpoints

| Group | Path | Auth | Description |
|-------|------|------|-------------|
| Root | `GET /` | No | Server identifier |
| Docs | `GET /docs` | No | Swagger UI |
| Docs | `GET /docs/openapi.json` | No | OpenAPI spec |
| Auth | `POST /user/login` | No | Login (returns JWT + cookie) |
| Auth | `POST /user/logout` | Yes | Logout |
| Auth | `GET /user/profile` | Yes | User profile |
| Managed | `POST /managed/request` | Yes | CRUD (create/update/delete/move) |
| Managed | `POST /managed/query` | Yes | Query entries |
| Managed | `GET /managed/entry/{type}/{space}/{subpath}/{shortname}` | Yes | Get single entry |
| Managed | `POST /managed/resource_with_payload` | Yes | Upload with file |
| Managed | `POST /managed/csv` | Yes | CSV export |
| Managed | `POST /managed/resources_from_csv/{type}/{space}/{subpath}/{schema}` | Yes | CSV import |
| Managed | `PUT /managed/progress-ticket/{space}/{subpath}/{shortname}/{action}` | Yes | Workflow state transition |
| Public | `POST /public/query` | No | Public query |
| Public | `POST /public/submit/{space}/{schema}/{subpath}` | No | Public submission |
| Info | `GET /info/manifest` | Yes | Server manifest + plugins |
| Info | `GET /info/settings` | Yes | Effective settings |
| WebSocket | `GET /ws?token=<jwt>` | Token | Real-time channel subscriptions |

## Client libraries

Official SDKs for talking to the dmart REST API:

| Language / runtime | Package | Install |
|---|---|---|
| Python | [`pydmart`](https://pypi.org/project/pydmart/) | `pip install pydmart` |
| Python | [`dmart`](https://pypi.org/project/dmart/) (core + CLI) | `pip install dmart` |
| TypeScript / JavaScript (Node, Deno, Bun, browsers) | [`@edraj/tsdmart`](https://www.npmjs.com/package/@edraj/tsdmart) | `npm install @edraj/tsdmart` |
| Dart / Flutter | [`dmart`](https://pub.dev/packages/dmart) | `flutter pub add dmart` |

MCP-capable AI agents (Zed, Claude Code, Cursor, …) can connect directly
to `/mcp` on any dmart instance — no SDK needed. See
[docs/plugins-and-mcp.md](./docs/plugins-and-mcp.md).

## Plugin System

### Built-in Plugins

Compiled into the binary. Configured via `plugins/<name>/config.json`:

- `resource_folders_creation` — auto-creates `/schema` folder on space creation
- `realtime_updates_notifier` — WebSocket broadcasts on CRUD events
- `audit` — logs all dispatched events
- `db_size_info` — API plugin at `GET /db_size_info/`

### External Native Plugins

Drop a `.so` + `config.json` into `~/.dmart/plugins/<name>/` — no recompile needed:

```bash
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

### Building Custom Plugins

1. Build
```
cd ~/projects/open-source/csdmart/custom_plugins_sdk/<name>                                                                                                                
dotnet publish <name>.csproj -c Release -r linux-x64 -o /tmp/<name>-build
```

2. Drop in the new .so (+ source files if you want them co-located)
```
cp /tmp/<name>-build/<name>.so ~/.dmart/plugins/<name>/                                                                                                                    
cp Plugin.cs <name>.csproj ~/.dmart/plugins/<name>/
```


Plugins export C-ABI functions: `get_info()`, `hook()` or `handle_request()`, `free_string()`. Can be written in any language (C#, Rust, C, Go). See [custom_plugins_sdk/README.md](custom_plugins_sdk/README.md) for the full development guide with working examples.

## Building

```bash
# Development
dotnet run -- serve

# Production (AOT native binary)
./build.sh
# Output: bin/dmart (single ~35MB binary)

# RPM packages
./dist/build-rpm.sh          # Fedora
./dist/build-rpm.sh el9      # RHEL 9 (via podman)
./dist/build-rpm.sh srpm     # Source RPM
```

## Testing

```bash
# Unit + integration tests (450+ tests)
dotnet test dmart.Tests/dmart.Tests.csproj -c Release

# E2E smoke tests (90 checks)
DMART_URL=http://localhost:5099 ./curl.sh
```

See [docs/testing.md](./docs/testing.md) for fixtures, parallelism rules,
and the commands that include DB-backed integration tests.

## Documentation

Engineering reference for maintainers and contributors, with Mermaid
architecture diagrams:

- [docs/README.md](./docs/README.md) — navigation
- [GLOSSARY.md](./GLOSSARY.md) — DMART-specific terms (Entry, Space, CXB, MCP, …)
- [docs/architecture.md](./docs/architecture.md) — layers, request lifecycle, startup sequence
- [docs/data-model.md](./docs/data-model.md) — ER diagram, wire format rules, repositories
- [docs/permissions.md](./docs/permissions.md) — the permission walk, anonymous + world, ACL, conditions
- [docs/auth.md](./docs/auth.md) — login/JWT/session flows, OAuth providers, invitations
- [docs/plugins-and-mcp.md](./docs/plugins-and-mcp.md) — plugin lifecycle + MCP protocol + OAuth discovery
- [docs/query.md](./docs/query.md) — query types, search syntax, sort_by, ACL filtering
- [docs/testing.md](./docs/testing.md) — xUnit + curl.sh + parallelism + common recipes
- [docs/debugging.md](./docs/debugging.md) — known pitfalls, AOT gotchas, SQL inspection
- [docs/contributing.md](./docs/contributing.md) — recipes: add endpoint, repository, service, plugin

## Deployment

### Systemd (RPM)

```bash
sudo dnf install ./dmart-*.rpm
sudo vi /etc/dmart/config.env
sudo systemctl enable --now dmart

# Logs
journalctl -u dmart -f
```

### Docker (all-in-one)

```bash
./admin_scripts/docker/notes.sh
# Includes: PostgreSQL 18 + dmart
# Access: http://localhost:8000/cxb/
```

## Project Structure

```
dmart.csproj              # Main project (AOT binary)
Program.cs                # Entry point + CLI subcommands
Api/                      # HTTP endpoint handlers
  Managed/                # Authenticated CRUD/query/upload
  Public/                 # Unauthenticated endpoints
  User/                   # Auth/profile/OTP
  Info/                   # Manifest/settings
Services/                 # Business logic
  QueryService.cs         # Query dispatch
  EntryService.cs         # CRUD with hooks
  PermissionService.cs    # Access control
  UserService.cs          # Login/session/lockout
DataAdapters/Sql/         # PostgreSQL repositories
Models/                   # Core entities + API models
Plugins/                  # Plugin system
  BuiltIn/                # Compiled plugins
  Native/                 # External .so plugin loader
Middleware/                # CORS, CXB, logging, headers
Auth/                     # JWT, Argon2, OTP
Cli/                      # Interactive CLI client
Config/                   # Settings + dotenv parser
dist/                     # RPM spec, systemd, completions
admin_scripts/            # Docker, Ansible
custom_plugins_sdk/       # Sample native plugin projects
```

## Technology Stack

- **.NET 10** with Native AOT (single binary, no runtime needed)
- **PostgreSQL 13+** — schema lives in `DataAdapters/Sql/SqlSchema.cs`
- **Svelte** (CXB admin frontend, embedded in binary)
- **PostgreSQL** client libs (Npgsql, no ORM)

## License

AGPL-3.0
