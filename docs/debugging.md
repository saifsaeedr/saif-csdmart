# Debugging

Every gotcha here cost real debugging time. When something breaks in a way
that "should just work," scan this list before guessing.

## Operational debugging

### Server won't start: "Port already in use"

`Program.cs` runs a pre-flight socket bind before `app.Run()`. If another
dmart (or anything) is on the port, you get a clean error and exit code 1.
Find and stop it:

```bash
# Find the process
ss -tlnp | grep 5099
# Or:
lsof -iTCP:5099 -sTCP:LISTEN

# Kill (only a published dmart binary, not the bash you're running in)
ps auxww | grep -E '/publish/dmart' | grep -v grep
```

### Tests fail with `Account has been locked due to too many failed login attempts`

The `Account_Lockout_After_Max_Failed_Attempts` test sets
`users.attempt_count = 5` on the admin and racing parallel tests get
blocked. Reset before rerunning:

```sql
UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';
```

### Tests fail with `WWW-Authenticate: Bearer error="invalid_token", error_description="The signature key was not found"`

Signing key mismatch. The JwtBearer options were captured at
services-build time before the test config was applied. Fix is in
`Auth/JwtBearerSetup.cs` — MUST use lazy configuration
`AddOptions<JwtBearerOptions>().Configure<IOptions<DmartSettings>>(...)`
so the options builder reads settings at first-use, not at registration
time.

Alternatively if the error is in production, verify that
`JWT_SECRET` hasn't rotated out from under live session JWTs.

### Queries return 0 results despite entries existing

Walk the permission tree. See [permissions.md](./permissions.md) for the
SQL snippets. Most common causes:

1. No `anonymous` user row + trying `/public/query`. Python parity =
   anonymous needs a row + at least one role.
2. Permission's `subpaths` stored with leading slash (`"/items"`), and
   you're on a pre-slash-normalization build. Rebuild or patch
   `PermissionService.NormalizePermissionSubpath`.
3. Permission carries `conditions:["is_active"]` but actions list doesn't
   include `"query"`. The `view` probe fails the condition check without
   a loaded resource. Add `query` to the permission's actions list.
4. `filter_schema_names: ["xxx"]` where entries don't have that schema
   in `payload->>'schema_shortname'`.
5. ACL filter at SQL level rejecting everything — happens when
   `query_policies` isn't properly maintained on the entries.

### Queries return the wrong order

`sort_by` is accepted on wire but only columns in the per-table whitelist
or valid JSON paths map to an emitted `ORDER BY`. Unknown values silently
fall back to `updated_at`. See `QueryHelper.ResolveSortToken`. Whitelist
lives in `SharedSortColumns` + `TableSortColumns`.

If you want numeric order on a JSON payload field, make sure you're on
a build that has the CASE-numeric wrap (post-sort_by-upgrade). Older
builds did plain text sort → `1, 10, 2` lex order.

### 422 + `INVALID_ROUTE` on a path you expected to work

Every unmatched route except `/cxb/*` gets transformed to 422 by the
middleware at the bottom of `Program.cs`. If that path is supposed to be
served by a plugin, the plugin may not be loaded — check `/info/manifest`
for the active plugin list, or the server log for `PLUGIN_LOADED` /
`PLUGIN_SKIPPED` lines.

### CXB SPA returns 422 on deep links

Same as above. CXB SPA paths (`/cxb/some/deep/route`) should return the
rewritten index.html. If they return 422, either:
- CXB isn't bundled (no file provider found) → `/cxb/*` returns natural
  404 and the INVALID_ROUTE middleware should skip per the cxbPath check.
  Verify `Program.cs` excludes the CXB prefix.
- The path prefix doesn't match `CXB_URL` settings. Double-check
  `settings.CxbUrl`.

### HTTP 500 with empty body on a bind error

Minimal API swallows JSON body-binding failures as 400-no-body. Our
handlers parse manually via
`JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.X, ct)`
inside a try/catch. See `Api/Public/QueryHandler.cs`. If you add a new
handler, use `HttpRequest req` + manual parse for any non-trivial body.

### `{"status":"failed","error":{"type":"jwtauth","code":49,...}}`

"Not authenticated" — no valid JWT. If you sent a cookie but no
Authorization header, verify the cookie is named exactly `auth_token`
and has `Path=/`. If using Postman/curl, pass `-H "Authorization: Bearer $TOKEN"`.

Also note: `/user/login` writes the cookie; subsequent requests can omit
the header. To log out, `POST /user/logout` clears the cookie and
deletes the session row.

### Intermittent test failures on shared rows

Tests that touch `anonymous` or `world` rows race if run in parallel.
Make sure the class is in `[Collection(AnonymousWorldCollection.Name)]`.
See [testing.md](./testing.md).

## AOT + source-gen JSON gotchas

### `Dictionary<string, object>` values with unregistered runtime types

Source-gen JSON serializes each value via its runtime type. Types that
must be registered in `DmartJsonContext` or dropped:

- `long` — Postgres `COUNT(*)` returns it; cast to `int` before insert.
- `string[]` — use `List<string>`.
- Nested `Dictionary<string, object>` — values need to be registered types.
- `JsonElement` — must be explicitly added to `DmartJsonContext`.

Symptom: `JsonException: Metadata for type … was not provided`.

### Property initializers silently drop

```csharp
public bool RetrieveTotal { get; init; } = true;  // ← initializer ignored
```

If the incoming JSON omits the key, the property is set to `default(T)` =
`false`. Any field with a non-default intended value must be nullable
(`bool?`), with the consumer interpreting `null` as the default.

Audit when you see surprising zero/false behavior on optional wire fields.

### `DefaultIgnoreCondition` attribute doesn't stick

`[JsonSourceGenerationOptions(DefaultIgnoreCondition = WhenWritingNull)]`
only drives TypeInfo metadata, not `SerializerOptions` at runtime. Nulls
(`error: null`, `attachments: null`) leak onto the wire unless
`ConfigureHttpJsonOptions` also sets
`o.SerializerOptions.DefaultIgnoreCondition = WhenWritingNull`.

### `JsonSerializer.SerializeToElement<T>` triggers IL2026/IL3050

Use `Utf8JsonWriter` + `JsonDocument.Parse` for manual element
construction. See `ResourceWithPayloadHandler.StringJsonElement()`.

### `UseStringEnumConverter = true` emits raw member names

Source-gen ignores `[EnumMember]`. Use the custom
`EnumMemberConverterBase<T>` in `Models/Json/EnumMemberConverter.cs` with
one concrete subclass per enum.

### `JwtSecurityTokenHandler` is off-limits

AOT-hostile (uses reflection). Use `Microsoft.IdentityModel.JsonWebTokens`
(`JsonWebTokenHandler`) + set `IssuerSigningKeyResolver` because our
tokens don't carry a `kid` header.

## NPGSQL + PG gotchas

### `Npgsql.EnableLegacyTimestampBehavior`

Must be the very first line of `Program.cs`. Without it,
`timestamp without time zone` columns reject `DateTime.UtcNow` (Kind=Utc).

### `ON CONFLICT` on spaces

`spaces` has a 3-column UNIQUE constraint `(shortname, space_name, subpath)`,
not just `shortname`. `SpaceRepository.UpsertAsync` uses the tuple on
conflict. Don't "simplify" it or you'll get `42P10: there is no unique or
exclusion constraint matching the ON CONFLICT specification`.

### Materialized views need `REFRESH CONCURRENTLY`-capable unique indexes

`mv_user_roles` and `mv_role_permissions` have `idx_mv_*_unique` indexes.
Without them, `REFRESH MATERIALIZED VIEW CONCURRENTLY` fails. Both
dmart Python and our `SqlSchema.cs` create them.

### `CREATE TABLE IF NOT EXISTS` doesn't add columns to existing tables

If dmart Python provisioned a DB earlier, older column sets linger.
`SqlSchema.cs` has a **forward-compat patches block** at the bottom with
idempotent `ALTER TABLE … ADD COLUMN IF NOT EXISTS` statements. Every
time you reference a new column in a SELECT/INSERT, add a matching
`ADD COLUMN IF NOT EXISTS` to that block — the CREATE TABLE block is
only consulted on brand-new DBs.

### `filter_schema_names=["meta"]` is a sentinel, not a filter

`QueryHelper.BuildWhereClause` strips `"meta"` from the list before
applying the filter. Internal callers that want all entries MUST pass
`FilterSchemaNames = new()` (empty list).

See `Services/ImportExportService.ExportAsync` for the canonical example.

### Permission `owner_shortname` FK to `users.shortname`

Tests that upsert Permission rows directly MUST set `OwnerShortname` to
an existing user (`cstest`, `dmart`). Using placeholders like
`"test_harness"` fails with
`23503: insert or update on table "permissions" violates foreign key
constraint "permissions_owner_shortname_fkey"`.

## Shell / OS gotchas

### `pkill -f 'dmart'` from this project dir kills your own bash

Bash command lines contain the project path — `/home/…/dmart` — so
`pkill -f 'dmart'` matches the running shell. Exit code 144 / "killed"
with no log file created.

Safer patterns:
- `pkill -f '/publish/dmart '`
- `pkill -f 'dmart serve'`
- Track PID: `SERVER=$!` and `kill $SERVER`

### Cold-start timing contaminated by bash snapshot

When I run `./bin/dmart` in background then poll readiness in a separate
`Bash` call, the polling shell has to source the bash snapshot first
(~10 s). The measured cold start is at LEAST that long even if dmart
actually came up in 100 ms.

Fix: do launch + poll in a single foreground command — the snapshot only
sources once, and `date +%s%N` inside that session is clean.

## Log format

`LOG_FORMAT=json` (default for LOG_FILE output) produces JSONL per line:

```json
{"Timestamp":"2026-04-19T20:35:22.259Z","EventId":0,"LogLevel":"Information",
 "Category":"Microsoft.Hosting.Lifetime","Message":"dmart v... on .NET 10.0.4",
 "State":{ ... }}
```

When `LOG_FILE` is set, access logs include the full request/response
body (capped at 32 KB per side) plus correlation ID, duration, user.
Secrets are redacted — see `Middleware/RequestLoggingMiddleware.cs` for
the field list.

Log levels: trace, debug, information, warning, error, critical, none.
Per-category overrides via `Logging:LogLevel:Category` in
`appsettings.json` or `Logging__LogLevel__Category` env var.

## PG inspection recipes

```bash
# Show the live schema
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart -c '\dt'

# Every user
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart \
  -c "SELECT shortname, is_active, attempt_count, roles FROM users;"

# A specific entry
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart \
  -c "SELECT * FROM entries WHERE space_name='X' AND subpath='/Y' AND shortname='Z';"

# Version + indexes
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart \
  -c "SELECT version_num FROM alembic_version;"
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart \
  -c "SELECT indexname FROM pg_indexes WHERE tablename='entries';"

# Force materialized view refresh (e.g. after a manual permission edit)
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart \
  -c "REFRESH MATERIALIZED VIEW mv_user_roles;"
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart \
  -c "REFRESH MATERIALIZED VIEW mv_role_permissions;"
```

## When something is still wrong

1. **Check the server log** — per-request JSONL when LOG_FILE is set;
   plain console lines otherwise. Access log includes the full error.
2. **Reproduce via curl** — strip the client layer, hit the endpoint
   directly. Include `-v` to see cookies / headers.
3. **Check the OpenAPI** — `GET /docs/openapi.json` or Swagger at `/docs`.
4. **Run `dmart settings`** — prints the effective redacted configuration.
5. **Bisect through history** — `git log --oneline` and test the last
   few commits.
6. **Read the Python source** — when a wire detail feels off, fetch
   `https://raw.githubusercontent.com/edraj/dmart/master/backend/<path>.py`
   or read the local uvenv install at `~/.uvenv/lib/python3.14/site-packages/dmart/`.
   Python is the spec.
