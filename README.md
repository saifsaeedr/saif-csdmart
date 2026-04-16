# DMART — Unified Data Platform (C# Port)

Native AOT port of [dmart](https://github.com/edraj/dmart) from Python to C# (.NET 10). Same PostgreSQL schema, same REST API, same CXB admin frontend — 30x faster on queries, 24x less RAM, 15x faster cold start.

## What is DMART?

DMART is a headless information management system for applications with small-to-medium data footprints (up to 300M entries). It provides:

- **Entry-oriented data model** — coherent information units with attachments, organized in hierarchical folders within spaces
- **Schema validation** — JSON Schema enforcement on content entries
- **Access control** — role-based permissions with per-entry ACLs, hierarchical subpath walks, and magic words (`__all_spaces__`, `__all_subpaths__`)
- **Workflow engine** — configurable state machines for ticket/task management
- **Plugin system** — built-in hooks + external native `.so` plugins loaded at runtime
- **WebSocket** — real-time notifications via channel subscriptions
- **Admin UI** — CXB Svelte SPA embedded in the binary

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
# Unit + integration tests (274 tests)
dotnet test dmart.Tests/dmart.Tests.csproj -c Release

# E2E smoke tests (80 checks)
DMART_URL=http://localhost:5099 ./curl.sh
```

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
- **PostgreSQL** (same schema as Python dmart)
- **Svelte** (CXB admin frontend, embedded in binary)
- **PostgreSQL** client libs (Npgsql, no ORM)

## License

AGPL-3.0
