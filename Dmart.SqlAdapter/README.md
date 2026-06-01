# Dmart.SqlAdapter

Direct PostgreSQL access to a dmart-managed database from any ASP.NET Core
project. C# equivalent of dmart Python's
[`data_adapters/sql_adapter.py`](https://github.com/edraj/dmart/blob/main/backend/data_adapters/sql_adapter.py).

This README is the integration guide: what to install, how to wire it into
ASP.NET Core, every configuration knob, and the gotchas you'll hit on
day one.

---

## Table of contents

1. [When to use this SDK](#when-to-use-this-sdk)
2. [Prerequisites](#prerequisites)
3. [Installation](#installation)
4. [Configuration](#configuration)
5. [RBAC enforcement](#rbac-enforcement)
6. [Dependency-injection registration](#dependency-injection-registration)
7. [Using the adapter in handlers](#using-the-adapter-in-handlers)
8. [JSON, AOT, and trimming notes](#json-aot-and-trimming-notes)
9. [Error handling and retries](#error-handling-and-retries)
10. [API surface (Python parity)](#api-surface-python-parity)
11. [Troubleshooting](#troubleshooting)

---

## When to use this SDK

Use `Dmart.SqlAdapter` when your service needs to read or write dmart's data
**without going through dmart's HTTP API**: admin tooling, bulk imports, ETL
jobs, analytics dashboards, internal microservices that share the same trust
boundary as the dmart instance.

Use [`Dmart.Client`](../dmart.Client/README.md) (HTTP SDK) instead when you
need permission enforcement, plugin dispatch, schema validation, workflow
transitions, or anything else the dmart server layers on top of the raw
database.

## Parity with `Dmart.Client`

Both SDKs expose the same dmart feature surface that other dmart SDKs
([`pydmart`](https://github.com/edraj/pydmart),
[`tsdmart`](https://github.com/edraj/tsdmart)) wrap — same DTOs
(`Dmart.Models`: `Entry`, `User`, `Space`, `Query`, `Locator`,
`Translation`, `Payload`, …), same method names, same return shapes.
Most consumers can swap one for the other by changing the constructor
and namespace import:

| Feature                       | SqlAdapter (DB) | Client (HTTP)   |
|-------------------------------|-----------------|-----------------|
| **Entry CRUD**                |                 |                 |
| `LoadAsync` / `LoadOrNoneAsync` / `IsEntryExistAsync` | yes | yes |
| `GetByUuidAsync` / `GetBySlugAsync` | yes        | yes             |
| `GetEntryByCriteriaAsync`     | yes             | yes (best-effort) |
| `GetSchemaAsync`              | yes             | yes             |
| `SaveAsync` / `CreateAsync` / `UpdateAsync` | yes | yes           |
| `DeleteAsync` / `MoveAsync`   | yes             | yes             |
| **Query**                     |                 |                 |
| `QueryAsync` → `(Total, Records)` | yes         | as `QueryEntriesAsync` |
| `GetSpacesAsync` (typed dict) | yes             | as `LoadSpacesAsync` |
| `FetchSpaceAsync`             | yes             | yes             |
| `GetChildrenAsync`            | yes             | yes             |
| **User / profile**            |                 |                 |
| `LoadUserMetaAsync`           | yes             | yes             |
| `GetProfileAsync(actor)`      | yes             | yes (`GET /user/profile`) |
| `GetUserPermissionsAsync`     | yes             | n/a — read `roles` on profile |
| **History**                   |                 |                 |
| `QueryHistoryAsync`           | yes (`histories` table) | yes (`QueryType.History`) |
| `AppendHistoryAsync`          | yes             | n/a — server auto-writes |
| **Locks**                     |                 |                 |
| `TryLockAsync` / `UnlockAsync`| yes             | yes (parity-shape over endpoints) |
| `GetLockerAsync`              | yes             | n/a — no HTTP endpoint |
| **Bootstrap**                 |                 |                 |
| `InitializeSpacesAsync`       | yes             | n/a — server bootstrap |

**Auth — Client-only.** `LoginAsync` / `LoginByAsync`, `LogoutAsync`,
`CreateUserAsync` (register), `UpdateUserAsync`, `CheckExistingAsync`,
`DeleteAccountAsync`, all OTP / password-reset variants
(`OtpRequestAsync`, `OtpRequestLoginAsync`, `PasswordResetRequestAsync`,
`ConfirmOtpAsync`, `ValidatePasswordAsync`), and the
social mobile-login wrappers (`GoogleMobileLoginAsync`,
`FacebookMobileLoginAsync`, `AppleMobileLoginAsync`) all live on
`Dmart.Client`. The SqlAdapter deliberately omits them because
authentication is the server's responsibility — a direct-DB caller is
already inside the trust boundary.

**Server-only operations.** Some dmart features require the running server
and aren't reachable from the SqlAdapter at all: plugin dispatch, schema
validation, workflow transitions (`ProgressTicketAsync`, `SubmitAsync`),
multipart file payloads (`UploadWithPayloadAsync`, `GetPayloadAsync`,
`GetAttachmentUrl`, `FetchDataAssetAsync`), CSV import/export
(`CsvAsync`, `ResourcesFromCsvAsync`, `ImportAsync`, `ExportAsync`),
short links, info introspection (`GetManifestAsync`, `GetSettingsAsync`,
`GetSpaceHealthAsync`, `GetInfoMeAsync`), admin
(`ReindexEmbeddingsAsync`, `ApplyAlterationAsync`,
`ReloadSecurityDataAsync`, `SemanticSearchAsync`), WebSocket admin,
MCP, QR, OAuth callbacks. Reach for `Dmart.Client` when you need any
of these.

### What this module is NOT

| It is NOT… | Because… |
|---|---|
| Part of the dmart server binary | The folder is excluded from `dmart.csproj` and `.dockerignore` — the AOT release does not ship it |
| A schema validator or workflow engine | JSON Schema enforcement, workflow transitions, and plugin dispatch live in the dmart server — calls that need those should still go through the HTTP API |
| AOT-friendly out of the box | JSONB serialization uses reflection-based `System.Text.Json` |
| A file-payload handler | Methods that depend on dmart's file-system tree (`save_payload`, `load_resource_payload`, `get_media_attachment`) are intentionally omitted |

The SDK **does** enforce RBAC when constructed with a `PermissionEngine` —
see [RBAC enforcement](#rbac-enforcement). The role/ACL/query-policy contract
matches the dmart HTTP API one-to-one.

---

## Prerequisites

- **.NET 8.0 or .NET 10.0 SDK** on the build machine.
- A running **PostgreSQL** instance that already hosts a dmart schema (created
  by the dmart server's `SchemaInitializer` or by Python's `create_tables.py`).
  This SDK does NOT create the schema — it consumes it.
- The Postgres user passed to the adapter needs SELECT/INSERT/UPDATE/DELETE on
  at least `entries`, `users`, `spaces`, `roles`, `permissions`, and
  `user_permissions_cache`.

Verify the schema is present before you start:

```bash
psql -h <host> -U <user> -d <db> -c "\dt entries users spaces"
```

If those three tables don't exist, run the dmart server once against this
database — it will create them — then come back.

---

## Installation

```sh
dotnet add package Dmart.SqlAdapter
```

`Dmart.Models` (the shared wire types) and `Npgsql` come along as
dependencies — no other ORM (EF Core, Dapper, …) is pulled in.

---

## Configuration

The adapter only needs one thing: how to reach Postgres. Configure it in
whichever style your project already uses.

### Style 1 — Raw connection string (simplest)

```csharp
var adapter = new DmartSqlAdapter(
    "Host=localhost;Port=5432;Username=dmart;Password=secret;Database=dmart");
```

Recommended when you want full control over every Npgsql tuning knob (timeouts,
keepalive, SSL, pooling) — pass the raw string and let Npgsql parse it.

### Style 2 — Strongly-typed `DmartDbOptions`

Use when you want to hydrate the config from `IConfiguration` /
`appsettings.json`. Each property maps one-to-one to a dmart Python `config.env`
key, so you can drop a Python deployment's settings in unchanged.

```csharp
var adapter = new DmartSqlAdapter(new DmartDb(new DmartDbOptions
{
    Host        = "localhost",
    Port        = 5432,
    Username    = "dmart",
    Password    = "secret",
    Database    = "dmart",
    PoolSize    = 20,
    MaxOverflow = 10,
    PoolTimeout = 30,
    PoolRecycle = 300,
}));
```

#### Every option, what it does, default

| Property            | Type   | Default      | Maps to (Python `config.env`)  | Notes |
|---------------------|--------|--------------|--------------------------------|-------|
| `ConnectionString`  | string | `null`       | `DATABASE_URL`                 | If set, all individual fields below are ignored. |
| `Host`              | string | `localhost`  | `DATABASE_HOST`                | Postgres host or socket path. |
| `Port`              | int    | `5432`       | `DATABASE_PORT`                | |
| `Username`          | string | `dmart`      | `DATABASE_USERNAME`            | |
| `Password`          | string | (empty)      | `DATABASE_PASSWORD`            | |
| `Database`          | string | `dmart`      | `DATABASE_NAME`                | |
| `PoolSize`          | int    | `20`         | `DATABASE_POOL_SIZE`           | Combined with `MaxOverflow` into Npgsql's `MaxPoolSize`. |
| `MaxOverflow`       | int    | `10`         | `DATABASE_MAX_OVERFLOW`        | |
| `PoolTimeout`       | int    | `30`         | `DATABASE_POOL_TIMEOUT`        | Seconds to wait for a free pooled connection. |
| `PoolRecycle`       | int    | `300`        | `DATABASE_POOL_RECYCLE`        | Idle connection lifetime in seconds. |

### Style 3 — JSON serialization options (advanced)

JSONB columns (`tags`, `acl`, `payload`, `relationships`, `displayname`,
`description`, `roles`, `groups`, …) are round-tripped via `System.Text.Json`.
By default the adapter uses snake_case property naming and skips nulls on
write, matching dmart's wire format. Override by passing your own
`JsonSerializerOptions`:

```csharp
var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    TypeInfoResolver     = MyAppSourceGenContext.Default,  // AOT-friendly
};
var adapter = new DmartSqlAdapter(new DmartDb(connectionString), json);
```

---

## RBAC enforcement

`Dmart.SqlAdapter` enforces the same role/ACL/query-policy contract that
dmart's HTTP API enforces. Constructed with a `PermissionEngine`, every
public method takes an `actor` (the user's shortname) and rejects operations
the actor isn't authorized for — exactly as if the call had come in as a
`/managed/*` HTTP request signed by that user's JWT.

### Two construction modes

| Mode | How to build | What it does |
|---|---|---|
| **RBAC ON** *(recommended)* | `DmartSqlAdapter.WithRbac(db)` or `new DmartSqlAdapter(db, json, engine)` | Every method gates on the `actor` parameter via `PermissionEngine`. Reads filter by `query_policies`; writes check role grants + per-row ACL + conditions (`own`, `is_active`) + field restrictions. |
| **System context** | `new DmartSqlAdapter(db)` (no engine) | No checks. For migrations, ETL, bootstrap tooling, or any code that already lives inside the trust boundary. The `actor` parameter is ignored. |

The toggle is explicit at construction — you can't accidentally bypass RBAC
by forgetting to pass `actor`. If the engine is wired and `actor` is null
the engine treats the call as anonymous, mirroring the API's behavior for
unauthenticated requests.

### What gets enforced

| Operation        | Action checked | Resource context loaded? | Behavior on deny |
|------------------|----------------|--------------------------|-------------------|
| `LoadAsync`        | `view`         | yes (load → check)       | throws `DmartPermissionDeniedException` |
| `LoadOrNoneAsync`  | `view`         | yes                      | throws |
| `GetEntryByCriteriaAsync` | `view`  | yes                      | returns `null` (matches Query's silent filter — search-shaped call) |
| `IsEntryExistAsync`| `view`         | yes                      | returns `false` (won't leak existence to unauthorized callers) |
| `SaveAsync`        | `create` or `update` | yes when row exists | throws |
| `CreateAsync`      | `create`       | no (row doesn't exist yet)| throws |
| `UpdateAsync`      | `update`       | yes                      | throws |
| `DeleteAsync`      | `delete`       | yes                      | throws |
| `MoveAsync`        | `delete` (source) + `create` (target) | yes for source | throws |
| `QueryAsync`       | `query` + LIKE on `query_policies` | n/a | returns `(0, [])` |
| `FetchSpaceAsync`  | `view` on space | yes                     | throws |
| `GetSpacesAsync`   | `view` on each space | yes                | spaces filtered out |
| `LoadUserMetaAsync`| `view` on user row | yes                  | throws |
| `GetUserPermissionsAsync` | `view` on target user row | yes (for non-self) | throws |
| `InitializeSpacesAsync`   | `create` on management space | no | throws |

### Quick start with RBAC

```csharp
using Dmart.SqlAdapter;
using Dmart.SqlAdapter.Models;
using Dmart.SqlAdapter.Permissions;

// 1) Build the adapter with RBAC enabled.
var db = new DmartDb("Host=localhost;Username=dmart;Password=secret;Database=dmart");
var adapter = DmartSqlAdapter.WithRbac(db);

// 2) Every call now takes an actor. Pass the authenticated user's shortname.
try
{
    var entry = await adapter.LoadAsync(
        spaceName: "products",
        subpath: "/catalog",
        shortname: "widget_42",
        resourceType: DmartResourceType.Content,
        actor: "alice");
    // ... entry is non-null and visible to alice
}
catch (DmartPermissionDeniedException ex)
{
    // ex.Actor, ex.Action ("view"), ex.SpaceName/Subpath/Shortname/ResourceType
    // Map this to your HTTP 403 response if exposing the call.
}

// 3) Queries silently filter rows alice can't see.
var (total, records) = await adapter.QueryAsync(new DmartQuery
{
    SpaceName = "products",
    Subpath = "/catalog",
    Limit = 50,
}, actor: "alice");
// total/records reflect ONLY rows alice has a 'query' grant for.
```

### Mapping HTTP user → actor

Inside an ASP.NET Core handler, the actor usually comes from the JWT subject
claim. Resolve it once per request and forward to every adapter call:

```csharp
app.MapGet("/entry/{space}/{shortname}",
    async (HttpContext ctx, string space, string shortname, DmartSqlAdapter dmart) =>
{
    var actor = ctx.User.FindFirst("shortname")?.Value
                ?? ctx.User.Identity?.Name;
    try
    {
        var entry = await dmart.LoadAsync(space, "/", shortname, actor: actor);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }
    catch (DmartPermissionDeniedException)
    {
        return Results.Forbid();
    }
});
```

Wire a middleware once to convert `DmartPermissionDeniedException` to a 403
and you'll never have to write the try/catch by hand:

```csharp
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DmartPermissionDeniedException)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
    }
});
```

### Permission cache

`PermissionEngine` caches each actor's resolved `(user, roles, permissions)`
graph in-memory for 5 minutes. The server has a write-time invalidation hook
into `roles` and `permissions` writes; outside the server process we can't
listen for that, so the SDK time-bounds the cache instead.

If you've just edited roles/permissions and need the change to apply
immediately, call:

```csharp
adapter.Engine?.InvalidateAll();
// or for one actor:
adapter.Engine?.Invalidate("alice");
```

Pass a custom TTL when constructing the engine if 5 minutes is too long or
too short for your workload:

```csharp
var engine = new PermissionEngine(db, jsonOptions: null,
    cacheTtl: TimeSpan.FromMinutes(1));
var adapter = new DmartSqlAdapter(db, jsonOptions: null, engine);
```

### Anonymous (`null` actor)

When the engine is wired but `actor` is `null`, calls are evaluated as the
anonymous user — the same path the dmart server takes for `/public/*` HTTP
endpoints. Anonymous resolves the special `"anonymous"` user row and the
`"world"` permission. To opt your deployment into public reads, create an
`anonymous` user row with at least one role, then attach the `world`
permission with `__all_spaces__` / `__all_subpaths__` scoped to whatever
subset of resources should be public.

### Opting out for trusted code paths

Background workers, schema migrations, and bulk-import scripts shouldn't
need to enforce RBAC. Instantiate the adapter without an engine:

```csharp
var trusted = new DmartSqlAdapter(db);  // no engine → no checks
await trusted.SaveAsync(entry);
```

Keep the trusted code paths visibly separate from the RBAC-enabled ones —
don't share one adapter instance across both modes.

---

### `appsettings.json` example

```json
{
  "ConnectionStrings": {
    "Dmart": "Host=localhost;Port=5432;Username=dmart;Password=secret;Database=dmart;Maximum Pool Size=30"
  }
}
```

…or with per-field settings:

```json
{
  "Dmart": {
    "Host": "localhost",
    "Port": 5432,
    "Username": "dmart",
    "Password": "secret",
    "Database": "dmart",
    "PoolSize": 20,
    "MaxOverflow": 10
  }
}
```

### Environment variables

ASP.NET Core's standard `IConfiguration` env-var binding works out of the box.
Use `:` (POSIX) or `__` (Docker) as the path separator:

```bash
export Dmart__Host=db.internal
export Dmart__Password=$(cat /run/secrets/dmart_password)
export Dmart__Database=dmart_prod
# or
export ConnectionStrings__Dmart="Host=db.internal;Username=dmart;Password=...;Database=dmart"
```

---

## Dependency-injection registration

`DmartSqlAdapter` is **thread-safe and stateless** — register it as a
singleton. Npgsql connection pooling lives inside the connection string, not
inside the adapter, so a single instance scales fine.

### Minimal-API host (`Program.cs`) — RBAC enabled

```csharp
using Dmart.SqlAdapter;
using Dmart.SqlAdapter.Models;
using Dmart.SqlAdapter.Permissions;

var builder = WebApplication.CreateBuilder(args);

// Wire DmartDb from configuration, then build adapter + engine off of it.
builder.Services.Configure<DmartDbOptions>(builder.Configuration.GetSection("Dmart"));
builder.Services.AddSingleton<DmartDb>(sp =>
    new DmartDb(sp.GetRequiredService<IOptions<DmartDbOptions>>().Value));
builder.Services.AddSingleton<PermissionEngine>(sp =>
    new PermissionEngine(sp.GetRequiredService<DmartDb>()));
builder.Services.AddSingleton<DmartSqlAdapter>(sp =>
    new DmartSqlAdapter(
        sp.GetRequiredService<DmartDb>(),
        jsonOptions: null,
        engine: sp.GetRequiredService<PermissionEngine>()));

var app = builder.Build();

// Convert permission denials to 403s once, app-wide.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DmartPermissionDeniedException) { ctx.Response.StatusCode = 403; }
});
```

### Minimal-API host (`Program.cs`) — system context (no RBAC)

```csharp
// Background worker / migration / ETL — no actor, no checks.
builder.Services.AddSingleton(_ =>
    new DmartSqlAdapter(builder.Configuration.GetConnectionString("Dmart")!));
```

### Controller-based host (`Startup.cs` / `Program.cs`)

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddControllers();
    services.AddSingleton(_ =>
        new DmartSqlAdapter(Configuration.GetConnectionString("Dmart")!));
}
```

### Lifetime cheat-sheet

| Type                 | Recommended lifetime | Why |
|----------------------|----------------------|-----|
| `DmartSqlAdapter`    | Singleton            | Stateless; thread-safe; pooling is inside Npgsql |
| `DmartDb`            | Singleton            | Just a connection-string holder |
| `DmartDbOptions`     | Singleton (via `IOptions<T>`) | Standard `IOptions` pattern |

Do NOT register as Scoped or Transient unless you have a specific reason — you'll
spawn extra `NpgsqlConnection` instances per request without any benefit.

---

## Using the adapter in handlers

### Minimal-API

```csharp
// Helper: pull the actor (user's shortname) out of the request's JWT once.
static string? ActorOf(HttpContext ctx) =>
    ctx.User.FindFirst("shortname")?.Value ?? ctx.User.Identity?.Name;

app.MapGet("/entries/{space}/{shortname}",
    async (HttpContext ctx, string space, string shortname, DmartSqlAdapter dmart, CancellationToken ct) =>
{
    var entry = await dmart.LoadAsync(
        spaceName: space,
        subpath: "/",
        shortname: shortname,
        resourceType: DmartResourceType.Content,
        actor: ActorOf(ctx),
        ct: ct);

    return entry is null ? Results.NotFound() : Results.Ok(entry);
});

app.MapPost("/entries", async (HttpContext ctx, DmartEntry entry, DmartSqlAdapter dmart, CancellationToken ct) =>
{
    await dmart.SaveAsync(entry, actor: ActorOf(ctx), ct);
    return Results.Created($"/entries/{entry.SpaceName}/{entry.Shortname}", entry);
});

app.MapDelete("/entries/{space}/{shortname}",
    async (HttpContext ctx, string space, string shortname, DmartSqlAdapter dmart, CancellationToken ct) =>
{
    var deleted = await dmart.DeleteAsync(new DmartLocator
    {
        SpaceName = space,
        Subpath = "/",
        Shortname = shortname,
        ResourceType = DmartResourceType.Content,
    }, actor: ActorOf(ctx), ct: ct);
    return deleted ? Results.NoContent() : Results.NotFound();
});
```

### MVC controller

```csharp
[ApiController, Route("api/dmart")]
public class DmartController(DmartSqlAdapter dmart) : ControllerBase
{
    private string? Actor => User.FindFirst("shortname")?.Value ?? User.Identity?.Name;

    [HttpGet("query/{space}")]
    public async Task<IActionResult> Query(string space, [FromQuery] string subpath = "/",
        [FromQuery] int limit = 20, [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        var (total, records) = await dmart.QueryAsync(new DmartQuery
        {
            SpaceName = space,
            Subpath = subpath,
            Limit = limit,
            Offset = offset,
        }, actor: Actor, ct: ct);
        return Ok(new { total, records });
    }
}
```

### Background service (hosted)

```csharp
// Use the system-context adapter (no RBAC) for background work.
public class DmartSyncWorker(DmartSqlAdapter dmart, ILogger<DmartSyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var spaces = await dmart.GetSpacesAsync(ct: ct);
            log.LogInformation("Synced {Count} spaces", spaces.Count);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
}

builder.Services.AddHostedService<DmartSyncWorker>();
```

---

## JSON, AOT, and trimming notes

`Dmart.SqlAdapter` targets `net8.0;net10.0` and does NOT set
`IsAotCompatible=true`. The default JSONB serializer uses reflection-based
`System.Text.Json`, which trips `IL2026`/`IL3050` analyzers if you turn on
trimming or AOT in the consumer.

If you need a trimmed or AOT-published host, two options:

1. **Disable AOT for the assemblies that touch the adapter.** Most admin or
   ETL tools don't need AOT; the simplest fix is to leave AOT off for the
   service that wraps `Dmart.SqlAdapter`.
2. **Supply a source-gen `JsonTypeInfoResolver`.** Define a
   `JsonSerializerContext` for all your DTOs (and the `Dmart*` types from
   `Models/DmartTypes.cs`), then pass the corresponding
   `JsonSerializerOptions` to the adapter constructor.

---

## Error handling and retries

The adapter surfaces Postgres errors as `NpgsqlException` /
`PostgresException` — wrap calls in `try/catch` if your handler needs custom
error mapping.

For multi-statement transactions that race on row locks, use the
`ExecuteWithRetryOnDeadlockAsync` helper exposed via the `Db` property:

```csharp
await adapter.Db.ExecuteWithRetryOnDeadlockAsync(async ct =>
{
    await adapter.SaveAsync(entryA, ct);
    await adapter.SaveAsync(entryB, ct);
    return true;
}, cancellationToken);
```

It retries on Postgres `40P01` (deadlock detected) up to 3 attempts with
linear backoff (50 ms, 100 ms). Any other `PostgresException` bubbles up
unchanged.

---

## API surface (Python parity)

| Python (`SqlAdapter`)        | C# (`DmartSqlAdapter`)            |
|------------------------------|-----------------------------------|
| `load` / `load_or_none`      | `LoadAsync` / `LoadOrNoneAsync`   |
| `get_entry_by_criteria`      | `GetEntryByCriteriaAsync`         |
| `get_by_uuid` / `get_by_slug`| `GetByUuidAsync` / `GetBySlugAsync` |
| `save`                       | `SaveAsync`                       |
| `create`                     | `CreateAsync`                     |
| `update`                     | `UpdateAsync`                     |
| `delete`                     | `DeleteAsync`                     |
| `move`                       | `MoveAsync`                       |
| `is_entry_exist`             | `IsEntryExistAsync`               |
| `query`                      | `QueryAsync`                      |
| `get_children`               | `GetChildrenAsync`                |
| `fetch_space`                | `FetchSpaceAsync`                 |
| `get_spaces`                 | `GetSpacesAsync`                  |
| `load_user_meta`             | `LoadUserMetaAsync`               |
| `get_profile`                | `GetProfileAsync`                 |
| `get_user_permissions`       | `GetUserPermissionsAsync`         |
| `get_schema`                 | `GetSchemaAsync`                  |
| `query_history` / `append_history` | `QueryHistoryAsync` / `AppendHistoryAsync` |
| `try_lock` / `unlock` / `get_locker` | `TryLockAsync` / `UnlockAsync` / `GetLockerAsync` |
| `initialize_spaces`          | `InitializeSpacesAsync`           |

Methods that depend on dmart's file-system payload tree (`save_payload`,
`save_payload_from_json`, `load_resource_payload`, `get_media_attachment`)
are intentionally omitted — they don't apply when the caller lives outside
the dmart process.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `relation "entries" does not exist` | The Postgres database is empty / never bootstrapped by dmart | Run the dmart server once against this DB to create the schema. |
| `password authentication failed for user "dmart"` | Wrong creds or password not URL-encoded in raw connection string | Re-check `appsettings.json`, or switch to `DmartDbOptions` so Npgsql escapes it for you. |
| `Exception thrown: System.NotSupportedException: JsonSerializer.Serialize without JsonTypeInfo` at runtime | Host is published with AOT/trimming and the default reflection-based STJ was stripped | Pass a `JsonSerializerOptions` with a source-gen `TypeInfoResolver` to the constructor — see [JSON, AOT, and trimming notes](#json-aot-and-trimming-notes). |
| `Exception: 40P01 deadlock detected` after retries | Heavy concurrent writers on the same `entries` rows | Batch writes upstream, or wrap the operation in `Db.ExecuteWithRetryOnDeadlockAsync` (bumps to 3 retries). |
| `An entry with shortname '…' already exists` from `CreateAsync` | The (space, subpath, shortname) tuple already has a row | Use `SaveAsync` for upsert semantics or `UpdateAsync` to mutate the existing row. |
| `Maximum Pool Size reached` | Long-running connections leaked or under-sized pool | Confirm every `await using` is awaited; raise `PoolSize` + `MaxOverflow`. |
| `DmartPermissionDeniedException` on a call you expect to succeed | Actor lacks role/ACL grant; permission rows recently changed but engine cache still has the old set | Verify the actor's roles → permissions → subpaths grant the action+resource_type, then call `adapter.Engine?.Invalidate(actor)` (or `InvalidateAll()`) to force a re-resolve. |
| `QueryAsync` returns `(0, [])` for an actor you expect to see rows | Actor has no permission with the `query` action covering this (space, subpath, resource_type) | Add a permission row with `actions: ["query"]` to one of the actor's roles. Use `engine.BuildUserQueryPoliciesAsync(actor, space, subpath)` to debug the resolved policy list. |

---

## Cache invalidation

`PermissionEngine` caches the resolved (user → roles → permissions) view
in-process for 5 minutes. The adapter automatically invalidates the
relevant slice on writes that go through it:

- `SaveAsync` / `UpdateAsync` / `CreateAsync` / `MoveAsync` / `DeleteAsync`
  on a `User` row → `engine.Invalidate(shortname)` for that user, and the
  `GetUserPermissionsAsync` cache is dropped for that user too.
- The same writes on a `Role` or `Permission` row → `engine.InvalidateAll()`
  plus `InvalidateUserPermissionsCache()` since downstream impact is
  graph-wide (a permission edit can affect every role that references it).
- All other resource types → no invalidation, since they don't participate
  in the authorization graph.

**Manual invalidation is still required when:**

- You edit `users` / `roles` / `permissions` rows via raw SQL or via
  `adapter.Db` directly (the SDK can't observe those writes).
- A second process — including the dmart server itself — modifies these
  rows. The cache is per-`PermissionEngine` instance; cross-process
  invalidation needs an out-of-band trigger (a pub/sub channel, a forced
  recycle, or the manual call below).

```csharp
adapter.Engine?.Invalidate("alice");           // one user
adapter.Engine?.InvalidateAll();               // every user
adapter.InvalidateUserPermissionsCache("alice"); // user_permissions_cache slice
```

---

## Limitations and known gaps

- **Server-only features.** Schema validation (JSON Schema enforcement),
  workflow transitions, plugin dispatch, embeddings/semantic search, OAuth,
  WebSocket, MCP, QR generation, import/export orchestration, short-link
  generation, manifest/settings, security cache reload — these all live in
  the dmart server. Calls that need them must go through the HTTP API
  (use [`dmart.Client`](../dmart.Client/README.md) instead).
- **Not AOT-compatible.** JSONB serialization uses reflection-based STJ.
  Consumers publishing with `PublishAot=true` must supply a source-gen
  `JsonTypeInfoResolver` via `JsonSerializerOptions` — see
  [JSON, AOT, and trimming notes](#json-aot-and-trimming-notes).
- **Lock period is global, not per-call.** `TryLockAsync` /
  `GetLockerAsync` accept a `lockPeriodSeconds` argument for signature
  parity with `dmart.Client`'s lock methods, but the value is ignored. The
  adapter uses `DmartSqlAdapterOptions.LockPeriodSeconds` (default 300s,
  matching dmart's `settings.LockPeriod`) so concurrent callers can't
  purge each other's still-valid locks. Set the period at construction
  time via the options bag.
- **`PermissionEngine` cache is time-bound, not event-bound.** Default
  TTL is 5 minutes. Out-of-band edits to roles/permissions need
  `InvalidateAll()` (see *Cache invalidation* above).
- **`GetUserPermissionsAsync` reads a server-maintained cache.** The
  underlying `user_permissions_cache` table is regenerated by the dmart
  server's graph traversal; this SDK does not rebuild it. If the cache is
  empty for a user, run the dmart server's `reload-security-data` flow.
- **`LoadUserMetaAsync` redacts the password hash.** It returns the user
  with `Password = null`. Use `LoadUserMetaForAuthAsync` if you actually
  need the hash (login, password rotation).
- **No end-to-end `PermissionEngine.CanAsync` test coverage yet.** The
  parser, the static walk helpers, and the `PermissionFilter`
  LIKE-escaping are unit-tested. End-to-end CanAsync against a live
  Postgres is on the follow-up list.

---

## See also

- `sample/Program.cs` — a 40-line minimal-API example that loads, queries,
  and saves entries end-to-end.
- [`dmart.Client`](../dmart.Client/README.md) — the HTTP SDK; use it when
  you need permission enforcement.
- [`DataAdapters/Sql/`](../DataAdapters/Sql/) — the dmart server's own
  repositories, which this SDK mirrors. Read them for reference if you need
  to extend the surface (e.g. add `LockHandlerAsync` or
  `SavePayloadAsync`).
