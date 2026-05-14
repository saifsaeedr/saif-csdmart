// Minimal ASP.NET Core host that exposes dmart's database over HTTP using
// Dmart.SqlAdapter — with RBAC turned on so callers see exactly the rows
// they'd see going through the dmart HTTP API.
//
// To run:
//   cd Dmart.SqlAdapter/sample
//   dotnet new web -o .              (only if you want a runnable csproj here)
//   # then drop Program.cs in place and add a ProjectReference to ../Dmart.SqlAdapter.csproj
//   dotnet run
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.SqlAdapter;
using Dmart.SqlAdapter.Permissions;

var builder = WebApplication.CreateBuilder(args);

var connStr = builder.Configuration.GetConnectionString("Dmart")
              ?? "Host=localhost;Username=dmart;Password=dmart;Database=dmart";

// Build adapter with the permission engine wired — every operation now
// gates on the `actor` parameter against the dmart role / ACL / query-policy
// contract.
var db = new DmartDb(connStr);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(DmartSqlAdapter.WithRbac(db));

// (Real apps add their auth scheme here; this sample just reads the actor
// from a header so the example is self-contained.)
builder.Services.AddAuthorization();

var app = builder.Build();

// Translate permission denials to 403 once, app-wide.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DmartPermissionDeniedException) { ctx.Response.StatusCode = 403; }
});

static string? Actor(HttpContext ctx) =>
    ctx.User.FindFirst("shortname")?.Value
    ?? ctx.User.Identity?.Name
    ?? ctx.Request.Headers["X-Actor"].FirstOrDefault();  // sample-only fallback

// GET /spaces — only the spaces the caller has access to.
app.MapGet("/spaces", async (HttpContext ctx, DmartSqlAdapter dmart) =>
    Results.Ok(await dmart.GetSpacesAsync(actor: Actor(ctx))));

// GET /entry/{space}/{subpath}/{shortname} — load one entry. 403 if the
// caller can't view it; 404 if it doesn't exist.
app.MapGet("/entry/{space}/{subpath}/{shortname}",
    async (HttpContext ctx, string space, string subpath, string shortname, DmartSqlAdapter dmart) =>
{
    var entry = await dmart.LoadAsync(
        space, "/" + subpath.Replace('|', '/'), shortname, actor: Actor(ctx));
    return entry is null ? Results.NotFound() : Results.Ok(entry);
});

// GET /query/{space}/{subpath} — paged listing filtered by the actor's
// query policies. Rows the caller can't see are silently dropped.
app.MapGet("/query/{space}/{subpath}",
    async (HttpContext ctx, string space, string subpath, int limit, int offset, DmartSqlAdapter dmart) =>
{
    var (total, records) = await dmart.QueryAsync(new Query
    {
        Type = Dmart.Models.Enums.QueryType.Search,
        SpaceName = space,
        Subpath = "/" + subpath.Replace('|', '/'),
        Limit = limit > 0 ? limit : 20,
        Offset = offset,
    }, actor: Actor(ctx));
    return Results.Ok(new { total, records });
});

// POST /entry — upsert; throws DmartPermissionDeniedException if the actor
// lacks create/update on the target locator, caught by the middleware above.
app.MapPost("/entry", async (HttpContext ctx, Entry entry, DmartSqlAdapter dmart) =>
{
    await dmart.SaveAsync(entry, actor: Actor(ctx));
    return Results.Created($"/entry/{entry.SpaceName}/{entry.Subpath.TrimStart('/')}/{entry.Shortname}", entry);
});

app.Run();
