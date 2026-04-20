# Testing

Three layers, each with a distinct job:

| Layer | Where | Scope | Runs against |
|---|---|---|---|
| Unit | `dmart.Tests/Unit/` | Pure logic — SQL string generation, parsers, regex, matchers, mappers | No DB |
| Integration | `dmart.Tests/Integration/` | Services + repositories + HTTP host | Live PostgreSQL + in-process `WebApplicationFactory<Program>` |
| End-to-end smoke | `curl.sh` | Published binary against a real server | Live binary + live PostgreSQL |

Current counts: **452 xUnit + 90 curl.sh** (last verified 2026-04-19).

## Running tests

### xUnit

```bash
# All tests — needs a DB for integration tests. Without DB they auto-skip.
DMART_TEST_PG_CONN='Host=localhost;Username=dmart;Password=tramd;Database=dmart' \
DMART_TEST_PWD='Test1234' \
dotnet test dmart.Tests/dmart.Tests.csproj -c Release --nologo

# If tests have already been run: reset the admin's attempt_count first.
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart \
  -c "UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';"

# Filter by name
dotnet test dmart.Tests/ -c Release --nologo --filter "FullyQualifiedName~PermissionService"
```

### curl.sh

```bash
# Start the binary separately, then:
DMART_URL=http://127.0.0.1:5099 DMART_ADMIN=dmart DMART_PWD='Test1234' ./curl.sh

# Exit code = number of failed scenarios.
```

A helper that boots + smokes + kills in one session:

```bash
./build.sh
PGPASSWORD=tramd psql -h localhost -U dmart -d dmart \
  -c "UPDATE users SET attempt_count = 0 WHERE shortname = 'dmart';" >/dev/null

Dmart__PostgresConnection='Host=localhost;Username=dmart;Password=tramd;Database=dmart' \
  ASPNETCORE_URLS=http://127.0.0.1:5099 \
  ./bin/Release/net10.0/linux-x64/publish/dmart > /tmp/dmart.log 2>&1 &
SERVER=$!
for i in $(seq 1 50); do
  if curl -s --connect-timeout 0.1 --max-time 0.2 http://127.0.0.1:5099/ >/dev/null 2>&1; then break; fi
  sleep 0.1
done
DMART_URL=http://127.0.0.1:5099 DMART_ADMIN=dmart DMART_PWD='Test1234' ./curl.sh
kill -TERM $SERVER; wait $SERVER 2>/dev/null
```

**Do not** use `pkill -f 'dmart'` from inside the project directory — the
pattern matches your own shell's command line (the path contains "dmart")
and kills the bash process itself. Use a more specific pattern or store
`$SERVER` and `kill $SERVER`.

## The `DmartFactory` fixture

`dmart.Tests/Integration/DmartFactory.cs` inherits
`WebApplicationFactory<Program>`. One instance per test class (via
`IClassFixture<DmartFactory>`). Resolution order for the DB connection:

1. `DMART_TEST_PG_CONN` env var (full Npgsql string).
2. `config.env` in the current working directory (same file devs use).
3. `null` → DB-backed tests auto-skip via `HasPg`.

Admin creds: `DMART_TEST_ADMIN` / `DMART_TEST_PWD` env vars, or defaults
to `dmart` / `Test1234`. **`AdminShortname` is hardcoded to `"dmart"`** —
the `DMART_TEST_ADMIN` env var is cosmetic, since `AdminBootstrap` creates
`dmart` regardless.

## Writing tests

### Unit tests

Pure logic. No `IClassFixture`. Call static helpers directly. Example:

```csharp
public class QueryHelperTests
{
    [Fact]
    public void Search_PlainText_Generates_ILIKE()
    {
        var q = new Query { Type = QueryType.Subpath, SpaceName = "t", Subpath = "/", Search = "hello" };
        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(q, args);
        where.ShouldContain("ILIKE");
        where.ShouldContain("payload::text");
    }
}
```

Pattern: short, one-fact per test. Use `Shouldly` for readable assertions.

### Integration tests

`IClassFixture<DmartFactory>` + `if (!DmartFactory.HasPg) return;` guard
so the suite still runs without PG. Touch only what the test needs, and
clean up in `finally`:

```csharp
[Fact]
public async Task Example_Test()
{
    if (!DmartFactory.HasPg) return;
    var users = _factory.Services.GetRequiredService<UserRepository>();

    var name = $"itest_{Guid.NewGuid():N}"[..16];
    try
    {
        // setup
        await users.UpsertAsync(new User { ... Shortname = name, ... });
        // act + assert
    }
    finally
    {
        try { await users.DeleteAsync(name); } catch { }
    }
}
```

**For tests that mutate the reserved `anonymous` user or `world`
permission**, add `[Collection(AnonymousWorldCollection.Name)]` to the
class. This forces serialization — otherwise two test classes racing on
the same row produce flaky failures.

### HTTP-level tests

Use `_factory.CreateClient()`. `DefaultRequestHeaders.Authorization` for
authenticated calls; leave it unset for anonymous. `DmartJsonContext.Default.*`
for source-gen serialization. Example:

```csharp
var client = _factory.CreateClient();
var body = new Query { Type = QueryType.Search, SpaceName = space, Subpath = "/items", Limit = 10 };
var resp = await client.PostAsJsonAsync("/public/query", body, DmartJsonContext.Default.Query);
resp.StatusCode.ShouldBe(HttpStatusCode.OK);
var response = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
```

Response attributes (`total`, `returned`) round-trip through HTTP as
`JsonElement`, not `int`. Cast via `((JsonElement)response.Attributes!["total"]).GetInt32()`.

### curl.sh patterns

Each scenario is a printf + curl pair:

```bash
# ============================================================================
# NN. Short description
# ============================================================================
printf '%-45s' "Test name:" >&2
RESP=$(curl -s -H "$CT" -H "$AUTH_HEADER" "$API_URL/managed/..." -d '{...}')
if echo "$RESP" | jq -e '.status == "success"' > /dev/null 2>&1; then
    ok
else
    nope "$RESP"
fi
```

`ok` and `nope` are helpers defined at the top of `curl.sh`. Increment the
trailing counter on additions so the summary line stays accurate.

For scenarios that might not be available on every environment (plugin
tests, CXB tests), branch on HTTP status or response shape to skip gracefully:

```bash
elif _route_absent "$API_URL/sample_api/greet/TestUser"; then
    ok "(sample_api not deployed)"
```

`_route_absent` (in `curl.sh`) accepts either HTTP 404 OR HTTP 422 with
`error.code == 230` (INVALID_ROUTE) as "not deployed".

## xUnit parallelism

xUnit runs classes in parallel by default. Classes that mutate **shared
DB state** (especially the reserved `anonymous` user, `world` permission,
or the admin's `attempt_count`) must either:

1. Use unique per-test names + restore prior state on teardown, OR
2. Share an `[CollectionDefinition]` that forces sequential execution.

Current collections:

- `AnonymousWorldCollection` — serializes `PublicQueryAnonymousTests` and
  `PermissionServiceIntegrationTests`.

Tests that happen to hit the admin's `attempt_count` (set it to 5 to test
lockout) need manual reset between tests — the lockout test does this in
its own `finally`.

## CI

`.github/workflows/ci.yml` runs `dotnet test` + `curl.sh` on every push.
Notes from past CI diagnostics:

- CI runners don't bundle CXB, so `/cxb/*` returns natural 404. Tests skip
  on 404. My earlier INVALID_ROUTE middleware used to convert those to
  422 + code 230, breaking the skip. Fix: `Program.cs` excludes the CXB
  path prefix from the INVALID_ROUTE transform.
- Plugin tests (`sample_api`) skip on 404 OR 422+code 230. Same reason.

If a CI run fails with `Login failed for 'dmart': Account has been locked`,
it's the `Account_Lockout_After_Max_Failed_Attempts` test setting
`attempt_count=5` and a parallel test racing.

## Adding tests checklist

- [ ] Unit test for pure logic or a regex / matcher edge.
- [ ] Integration test that exercises the full DB path.
- [ ] HTTP-level test if the feature is client-facing.
- [ ] `curl.sh` scenario if it's a smoke-level thing (new endpoint,
      new header, new CLI subcommand).
- [ ] If the test mutates `anonymous`/`world`: `[Collection(...)]` tag.
- [ ] If the test sets `attempt_count` or other shared admin state:
      restore in `finally`.
- [ ] Run the full suite at least once before committing — flaky tests
      tend to show up only in the full parallel run.

## Debugging a single failing test

```bash
# One test by name
DMART_TEST_PG_CONN='...' DMART_TEST_PWD='Test1234' \
  dotnet test dmart.Tests/ -c Release --nologo \
  --filter "FullyQualifiedName~ClassName.MethodName"

# Verbose output + console logs
DMART_TEST_PG_CONN='...' DMART_TEST_PWD='Test1234' \
  dotnet test dmart.Tests/ -c Release --nologo \
  --filter "FullyQualifiedName~ClassName" \
  --logger "console;verbosity=detailed"
```

Add `_factory.Services.GetRequiredService<ILoggerFactory>()` to a test to
grab the logging stack; for SQL queries, temporarily set
`Dmart.Configuration.Logging.LogLevel.Debug` via the factory config override.

## Load testing

The xUnit + curl.sh suites cover correctness. Capacity and latency
testing is out-of-band — point an off-the-shelf HTTP load generator at
a running binary:

| Tool | Typical use |
|---|---|
| [Apache Benchmark (`ab`)](https://httpd.apache.org/docs/current/programs/ab.html) | Quick single-URL RPS snapshot. `ab -n 10000 -c 100 -H 'Authorization: Bearer $T' $URL`. |
| [Vegeta](https://github.com/tsenart/vegeta) | Scriptable attacks + latency histograms. `echo "GET $URL" \| vegeta attack -rate=500 -duration=30s \| vegeta report`. |
| [Locust](https://locust.io/) | Python-scripted mixed scenarios (login → query → CRUD). Useful when you need per-endpoint SLAs. |
| [k6](https://k6.io/) | JS-scripted scenarios with built-in thresholds; plays nice with CI. |

Recommendations:
- Keep a **real DB** underneath. In-memory mocks don't surface connection-pool or MV-refresh bottlenecks.
- Authenticate once, re-use the JWT. `/user/login` is rate-limited and its password-hashing step (Argon2id with `time_cost=3`) dominates if you retry it per request.
- Measure both `total` and `returned` — large `retrieve_total: true` queries dominate the cost due to the extra `COUNT(*)` roundtrip. `retrieve_total: false` halves the work.
- Watch `mv_user_roles` / `mv_role_permissions` refresh time under write-heavy workloads. `REFRESH MATERIALIZED VIEW CONCURRENTLY` is bounded by their row counts; if it starts dominating, pre-warm the user-access cache or throttle role/permission writes.
