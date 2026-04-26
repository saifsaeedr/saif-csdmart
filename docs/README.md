# dmart — Engineering documentation

This folder is a tour of the codebase for engineers who need to understand,
debug, or extend dmart. The top-level [README.md](../README.md) covers
install/run/CLI — this folder goes into the **how** and **why**.

## Reading order

| Start here if you want to… | Read |
|---|---|
| …get the big picture (what are the layers, how does a request flow) | [architecture.md](./architecture.md) |
| …understand how data is shaped on the wire vs in Postgres | [data-model.md](./data-model.md) |
| …figure out why a permission check decided the way it did | [permissions.md](./permissions.md) |
| …trace a login, JWT, cookie, session, or OAuth flow | [auth.md](./auth.md) |
| …add a plugin or integrate with the MCP surface | [plugins-and-mcp.md](./plugins-and-mcp.md) |
| …understand query semantics (search syntax, sort_by, aggregation) | [query.md](./query.md) |
| …drive dmart from the command line (REPL, scripts, JSON pipelines) | [cli.md](./cli.md) |
| …run or extend the test suite (xUnit + curl.sh) | [testing.md](./testing.md) |
| …debug a live issue | [debugging.md](./debugging.md) |
| …add a new endpoint, repository, service, or plugin | [contributing.md](./contributing.md) |

## One-line summary of each component

- **`Program.cs`** — CLI dispatcher + web host wiring. Subcommands (`serve`, `version`, `settings`, `set_password`, `export`, `import`, `init`, `cli`) live here; the server boot path configures DI, middleware, route groups, plugins, and the port check.
- **`Api/`** — ASP.NET Core minimal-API handlers grouped by surface (`Managed/`, `Public/`, `User/`, `Info/`, `Qr/`, `Mcp/`, `Oauth/`). Each handler parses the request, delegates to a service, and maps the result to a `Response`.
- **`Services/`** — Domain layer. One service per concern (`UserService`, `QueryService`, `PermissionService`, `EntryService`, `WorkflowService`, `ImportExportService`, etc.). No HTTP, no SQL — just orchestration.
- **`DataAdapters/Sql/`** — Npgsql repositories, one per table. `QueryHelper.cs` is shared search/sort/ACL/aggregation SQL generation.
- **`Models/`** — Flat records. `Api/` = wire DTOs, `Core/` = domain entities, `Enums/` = `[EnumMember]`-tagged enums, `Json/` = source-gen `DmartJsonContext`.
- **`Auth/`** — Password hashing (Argon2id), JWT issuer, invitation JWT, OAuth providers (Google/Facebook/Apple), JwtBearer setup, OTP provider, password rules.
- **`Middleware/`** — Custom ASP.NET middleware (CXB SPA + static, request logging, response headers, WebSocket).
- **`Plugins/`** — `PluginManager`, `IHookPlugin`/`IApiPlugin` interfaces, built-in plugins in `BuiltIn/`, native `.so` loader in `Native/`.
- **`Config/`** — `DmartSettings`, strict dotenv validator, `SettingsSerializer` (redacts secrets for `/info/settings`).
- **`Cli/`** — Interactive REPL + script runner for the `dmart cli` subcommand.
- **`Utils/`** — `DotEnv` parser, `JsonbHelpers`, `LogSink`, correlation ID helpers.
- **`dmart.Tests/`** — xUnit tests. `Unit/` for pure logic, `Integration/` for DB-backed flows.
- **`cxb/`** — Svelte SPA (admin UI), built into embedded resources.
- **`plugins/`** — Config (and optionally source) for the built-in and native plugins.
- **`curl.sh`** — 90 end-to-end HTTP scenarios. Runs against a live binary.

## Conventions

- **Wire format is snake_case everywhere**, `status: "success"|"failed"`, `error.code` int, cookie auth, `[EnumMember]` on every enum. See [data-model.md](./data-model.md) for the full spec.
- **Schema lives in `DataAdapters/Sql/SqlSchema.cs`** — CREATE TABLE + `ALTER TABLE ADD COLUMN IF NOT EXISTS` patches at the bottom for forward-compat migrations on existing DBs.
- **Native AOT publish is the ship target.** No reflection-based JSON, no `JwtSecurityTokenHandler`, no runtime IL emit. See [debugging.md](./debugging.md) for AOT gotchas.
- **`Response.Fail(int code, string message, string type, List<Dictionary<string, object>>? info = null)`** is the only way to fail. `code` is `InternalErrorCode.*` (integer codes enumerated in `Models/Api/InternalErrorCode.cs`). `type` is one of `auth`/`jwtauth`/`db`/`request`/`internal`/`qr`/`catchall`.
