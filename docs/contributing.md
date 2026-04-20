# Contributing

Recipes for the most common tasks. All assume you've read
[architecture.md](./architecture.md) and know the layer layout.

## Add a new HTTP endpoint

Pick the right group first:

| New endpoint | Group | File |
|---|---|---|
| Authenticated CRUD/query | `/managed/*` | `Api/Managed/ManagedEndpoints.cs` |
| Anonymous-allowed | `/public/*` | `Api/Public/PublicEndpoints.cs` |
| Login / profile / OTP / OAuth | `/user/*` | `Api/User/UserEndpoints.cs` |
| Server metadata | `/info/*` | `Api/Info/InfoEndpoints.cs` |
| QR codec | `/qr/*` | `Api/Qr/QrEndpoints.cs` |
| MCP tool / resource | `/mcp` | `Api/Mcp/McpTools.cs` |

### 1. Create a handler file

`Api/Managed/MyNewHandler.cs`:

```csharp
using Dmart.Models.Api;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class MyNewHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/my-endpoint", async (
            HttpRequest req,
            MyService svc,
            HttpContext http,
            CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name
                ?? throw new InvalidOperationException("auth required");

            // Manual body parse — Minimal API's binding swallows errors as 400-no-body
            MyRequestBody? body;
            try
            {
                body = await System.Text.Json.JsonSerializer.DeserializeAsync(
                    req.Body,
                    Dmart.Models.Json.DmartJsonContext.Default.MyRequestBody,
                    ct);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, "request");
            }

            if (body is null)
                return Response.Fail(InternalErrorCode.INVALID_DATA, "empty body", "request");

            // Delegate to service
            var result = await svc.DoSomethingAsync(body, actor, ct);
            return result.IsOk
                ? Response.Ok(attributes: new() { ["answer"] = result.Value! })
                : Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "request");
        });
    }
}
```

### 2. Wire it into the endpoint group

`Api/Managed/ManagedEndpoints.cs`:

```csharp
public static void MapManaged(this RouteGroupBuilder g)
{
    // existing ...
    MyNewHandler.Map(g);
}
```

### 3. Register the request DTO in `DmartJsonContext`

`Models/Json/DmartJsonContext.cs`:

```csharp
[JsonSerializable(typeof(MyRequestBody))]
[JsonSerializable(typeof(MyResponseShape))]
public partial class DmartJsonContext : JsonSerializerContext { }
```

### 4. Add the request/response models

`Models/Api/MyRequestBody.cs`:

```csharp
namespace Dmart.Models.Api;

public sealed record MyRequestBody
{
    public required string Target { get; init; }
    // ← remember: any `= <literal>` initializer is silently dropped on missing keys.
    //   Use nullable with null-means-default.
    public string? Mode { get; init; }
}
```

### 5. Add the route to the OpenAPI spec

Minimal APIs expose OpenAPI automatically. Add `.WithTags("Managed").WithName("MyEndpoint").Produces<Response>()` if you want the generated spec to look clean.

### 6. Add tests

- Unit test for any pure logic in the service.
- Integration test hitting the HTTP endpoint via `_factory.CreateClient()`.
- curl.sh scenario if the endpoint is smoke-worthy.

### 7. Sanity check

```bash
dotnet build -c Release -nologo
dotnet test dmart.Tests/ -c Release --nologo --filter "FullyQualifiedName~MyNewHandler"
./build.sh  # AOT publish must succeed
```

## Add a new repository method

### 1. Drop the method on the right repo

`DataAdapters/Sql/EntryRepository.cs` (or the matching repo):

```csharp
public async Task<Entry?> GetBySomethingAsync(string value, CancellationToken ct = default)
{
    await using var conn = await db.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(
        $"{SelectAllColumns} WHERE something = $1 LIMIT 1",
        conn);
    cmd.Parameters.Add(new() { Value = value });
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
}
```

### 2. If it's a Query path, route through `QueryHelper.RunQueryAsync`

```csharp
public Task<List<Entry>> QueryFooAsync(Query q, CancellationToken ct = default)
    => QueryHelper.RunQueryAsync(
        db, SelectAllColumns, q, Hydrate, ct,
        tableName: "entries");     // pass userShortname too for SQL-level ACL
```

### 3. If it touches a column not in SqlSchema's CREATE TABLE

Add an `ALTER TABLE … ADD COLUMN IF NOT EXISTS` to the forward-compat
patches block at the bottom of `SqlSchema.cs` — the CREATE block is only
consulted on brand-new DBs.

### 4. If it touches authz-relevant state

- Call `await refresher.RefreshAsync(ct)` after writes to users/roles/permissions.
- Call `await access.InvalidateAllCachesAsync(ct)` to bust the in-memory
  user-access cache.

### 5. Hydrate hygiene

- Use `JsonbHelpers.FromListString`, `FromJsonbDict`, etc. for JSONB
  columns. They handle the NOT NULL empty-array/object defaults.
- `DateTime.Kind` — rely on `EnableLegacyTimestampBehavior` (set at
  `Program.cs` line 1).

## Add a new service

```csharp
// Services/MyService.cs
namespace Dmart.Services;

public sealed class MyService(
    EntryRepository entries,
    PermissionService perms,
    ILogger<MyService> log)
{
    public async Task<Result<string>> DoSomethingAsync(
        MyRequestBody req,
        string actor,
        CancellationToken ct)
    {
        if (!await perms.CanReadAsync(actor, req.TargetLocator, ct))
            return Result<string>.Fail(InternalErrorCode.NOT_ALLOWED, "denied", "auth");

        var entry = await entries.GetAsync(...);
        // …
        return Result<string>.Ok("done");
    }
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddSingleton<MyService>();
```

Singleton is the default because most services are stateless + hold
repositories that are themselves singletons. Use scoped only when you
need per-request state.

## Add a new enum value

1. Add the member to the enum in `Models/Enums/*.cs` with `[EnumMember(Value="python_wire_value")]`.
2. If the enum appears in a DB column (e.g. `usertype`, `language`,
   `resource_type`), `JsonbHelpers.EnumNameLower<T>` uses the C# member
   name lowercased. Ensure the PG enum type accepts the new value —
   if the DB was provisioned by Python dmart, the PG enum type is
   authoritative.
3. Source-gen JSON uses the `[EnumMember]` value via
   `EnumMemberConverterBase<T>` — no other registration needed.
4. Update `curl.sh` and tests if the new value should appear in payloads.

## Add a new built-in plugin

### 1. Create a class implementing `IHookPlugin` or `IApiPlugin`

`Plugins/BuiltIn/MyPlugin.cs`:

```csharp
using Dmart.Models.Core;
using Dmart.Plugins;

namespace Dmart.Plugins.BuiltIn;

public sealed class MyPlugin(ILogger<MyPlugin> log) : IHookPlugin
{
    public string Shortname => "my_plugin";
    public bool AlwaysActive => false;   // obey filters
    public PluginListenTime Listen => PluginListenTime.After;

    public Task OnEventAsync(Event e, IServiceProvider sp, CancellationToken ct)
    {
        log.LogInformation("my_plugin saw {Action} on {Space}/{Subpath}/{Shortname}",
            e.ActionType, e.SpaceName, e.Subpath, e.Shortname);
        return Task.CompletedTask;
    }
}
```

### 2. Register in DI

`Program.cs`:

```csharp
builder.Services.AddSingleton<IHookPlugin, MyPlugin>();
```

`PluginManager` discovers them via `IEnumerable<IHookPlugin>` injection
— no manual wiring in the manager needed.

### 3. Add a config file (optional)

`plugins/my_plugin/config.json`:

```json
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
```

If no config is present, PluginManager uses reasonable defaults
(`is_active=true`, listen on everything).

## Add a new MCP tool

`Api/Mcp/McpTools.cs`:

```csharp
registry.RegisterTool(
    name: "dmart_my_tool",
    description: "What this tool does. Arguments: {foo:string, bar:int}.",
    handler: async (args, session, sp, ct) =>
    {
        var foo = args.GetProperty("foo").GetString();
        var svc = sp.GetRequiredService<MyService>();
        var result = await svc.DoSomethingAsync(new() { Target = foo! }, session.UserShortname, ct);
        return result.IsOk
            ? McpResult.TextContent(result.Value!)
            : McpResult.Error(result.ErrorMessage!);
    });
```

Tool names MUST use underscores (Anthropic's tool-use API rejects dots).

Each MCP tool runs as the logged-in user — permissions still apply. No
special escape hatch.

## Add a new curl.sh scenario

Append before the summary block in `curl.sh`:

```bash
# ============================================================================
# NN. Description
# ============================================================================
printf '%-45s' "Scenario name:" >&2
RESP=$(curl -s -H "$CT" -H "$AUTH_HEADER" "$API_URL/.../" -d '{...}')
if echo "$RESP" | jq -e '.status == "success" and .attributes.foo == "bar"' > /dev/null 2>&1; then
    ok
else
    nope "$RESP"
fi
```

If the scenario depends on optional components (a plugin, CXB), use the
`_route_absent` helper to skip gracefully on 404/422+code 230.

## Checklist before PR

- [ ] `dotnet build -c Release -nologo` succeeds with 0 warnings.
- [ ] `./build.sh` succeeds (AOT publish). 0 warnings.
- [ ] `dotnet test dmart.Tests/ -c Release` — all green, new tests added.
- [ ] `./curl.sh` — all green against a fresh server.
- [ ] `dmart settings` still produces valid JSON (new config keys are
      redacted if they hold secrets; see `SettingsSerializer.RedactedProperties`).
- [ ] OpenAPI at `/docs/openapi.json` validates (the minimal-API
      generator usually does the right thing, but check if you added
      headers or custom response types).
- [ ] No new `System.Reflection.*`, `JsonSerializer.SerializeToElement<T>`,
      `JwtSecurityTokenHandler`, or dynamic MVC features — all AOT-hostile.
- [ ] Any memory-touching change (DI registration, cache invalidation,
      MV refresh) preserves Python-parity semantics. See [permissions.md](./permissions.md)
      and [data-model.md](./data-model.md) for the non-obvious invariants.

## Where to ask for guidance

1. Check the docs in this folder.
2. `git log --oneline` for prior work on the same area.
3. `./curl.sh` — the canonical spec for expected HTTP-level behavior.
4. The Python source — `https://raw.githubusercontent.com/edraj/dmart/master/backend/<path>.py`
   — is the wire-parity spec.
