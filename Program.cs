using Dmart.Api.Info;
using Dmart.Api.Managed;
using Dmart.Api.Public;
using Dmart.Api.Qr;
using Dmart.Api.User;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Middleware;
using Dmart.Models.Json;
using Dmart.Plugins;
using Dmart.Plugins.BuiltIn;
using Dmart.Services;
using Microsoft.Extensions.Options;

// ============================================================================
// dmart CLI — mirrors Python's dmart.py multi-subcommand entry point.
//
// Usage:
//   dmart serve       Start the HTTP server (default if no subcommand)
//   dmart version     Print version info
//   dmart help        Print available subcommands
// ============================================================================

// Parse subcommand from args (first non-flag argument).
var subcommand = "serve";
var serverArgs = args;
if (args.Length > 0 && !args[0].StartsWith('-'))
{
    subcommand = args[0].ToLowerInvariant();
    serverArgs = args[1..];
}

switch (subcommand)
{
    case "version":
        Console.WriteLine("dmart-csharp 0.1.0 (AOT)");
        return;

    case "help":
    case "--help":
    case "-h":
        Console.WriteLine("""
            dmart — Structured CMS/IMS (C# port)

            Usage: dmart [subcommand] [options]

            Subcommands:
              serve          Start the HTTP server (default)
              version        Print version info
              help           Print this help

            Server options (via config.env or env vars):
              LISTENING_HOST     Listen address (default: 0.0.0.0)
              LISTENING_PORT     Listen port (default: 8282)
              DATABASE_HOST      PostgreSQL host
              DATABASE_PASSWORD  PostgreSQL password
              JWT_SECRET         JWT signing secret

            Config file lookup: $BACKEND_ENV → ./config.env → ~/.dmart/config.env
            """);
        return;

    case "serve":
    case "hyper":
        break; // fall through to server startup below

    default:
        Console.Error.WriteLine($"Unknown subcommand: {subcommand}");
        Console.Error.WriteLine("Run 'dmart help' for available subcommands.");
        Environment.ExitCode = 1;
        return;
}

// ============================================================================
// SERVER STARTUP (subcommand: serve / hyper)
// ============================================================================

// dmart Python uses `timestamp without time zone` columns. Npgsql 6+ rejects
// DateTime values with Kind=Utc against those columns unless this switch is set.
// This MUST run before any Npgsql operation, so it lives at the very top.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateSlimBuilder(serverArgs);

// AOT-friendly JSON: source-generated context. We REPLACE the default resolver
// (instead of inserting at position 0) so the framework's camelCase default policy
// can't override the snake_case policy declared on DmartJsonContext.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolver = DmartJsonContext.Default;
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// dmart-Python-style config.env support.
{
    var (envPath, values) = DotEnv.Load();
    if (envPath is not null && values.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(values);
        builder.Configuration.AddEnvironmentVariables();
    }

    // Wire LISTENING_HOST/LISTENING_PORT from config.env into Kestrel.
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    {
        var dmartSection = builder.Configuration.GetSection("Dmart");
        var host = dmartSection["ListeningHost"] ?? "0.0.0.0";
        var port = dmartSection["ListeningPort"] ?? "8282";
        builder.WebHost.UseUrls($"http://{host}:{port}");
    }
}

// Config
builder.Services.Configure<DmartSettings>(builder.Configuration.GetSection("Dmart"));
builder.Services.Configure<SpacesOptions>(builder.Configuration.GetSection("Spaces"));

// SQL backend
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<AuthzCacheRefresher>();
builder.Services.AddSingleton<EntryRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<AccessRepository>();
builder.Services.AddSingleton<AttachmentRepository>();
builder.Services.AddSingleton<HistoryRepository>();
builder.Services.AddSingleton<LockRepository>();
builder.Services.AddSingleton<LinkRepository>();
builder.Services.AddSingleton<OtpRepository>();
builder.Services.AddSingleton<CountHistoryRepository>();
builder.Services.AddSingleton<HealthCheckRepository>();
builder.Services.AddSingleton<SpaceRepository>();
builder.Services.AddHostedService<CountHistorySnapshotter>();

// Schema bootstrapper runs once on startup. AdminBootstrap MUST be registered AFTER
// SchemaInitializer — IHostedServices run StartAsync sequentially in registration order.
builder.Services.AddHostedService<SchemaInitializer>();
builder.Services.AddHostedService<AdminBootstrap>();

// Domain services
builder.Services.AddSingleton<PermissionService>();
builder.Services.AddSingleton<SchemaValidator>();
builder.Services.AddSingleton<EntryService>();
builder.Services.AddSingleton<QueryService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddSingleton<WorkflowService>();
builder.Services.AddSingleton<PluginManager>();
builder.Services.AddSingleton<LockService>();
builder.Services.AddSingleton<ShortLinkService>();
builder.Services.AddSingleton<CsvService>();
builder.Services.AddSingleton<ImportExportService>();
builder.Services.AddSingleton<QrService>();

// Auth
builder.Services.AddSingleton<JwtIssuer>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<OtpProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.GoogleProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.FacebookProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.AppleProvider>();
builder.Services.AddDmartAuth(builder.Configuration);

// Plugins
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IHookPlugin, ResourceFoldersCreationPlugin>();
builder.Services.AddSingleton<IHookPlugin, RealtimeUpdatesNotifierPlugin>();
builder.Services.AddSingleton<IHookPlugin, AuditPlugin>();
builder.Services.AddSingleton<IHookPlugin, AdminNotificationSenderPlugin>();
builder.Services.AddSingleton<IHookPlugin, SystemNotificationSenderPlugin>();
builder.Services.AddSingleton<IHookPlugin, LocalNotificationPlugin>();
builder.Services.AddSingleton<IHookPlugin, LdapManagerPlugin>();
builder.Services.AddSingleton<IApiPlugin, DbSizeInfoPlugin>();

// Per-request context
builder.Services.AddScoped<RequestContext>();

// GZip compression
builder.Services.AddResponseCompression(o => { o.EnableForHttps = true; });

var app = builder.Build();

// Exception handler
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json";
            var body = Dmart.Models.Api.Response.Fail("internal_error", ex.Message, "exception");
            await ctx.Response.WriteAsJsonAsync(body, DmartJsonContext.Default.Response);
        }
    }
});

// GZip
app.UseResponseCompression();

// Correlation ID
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Headers.ContainsKey("X-Correlation-ID"))
        ctx.Response.Headers["X-Correlation-ID"] = Guid.NewGuid().ToString("N");
    else
        ctx.Response.Headers["X-Correlation-ID"] = ctx.Request.Headers["X-Correlation-ID"].ToString();
    await next();
});

// Request timeout
app.Use(async (ctx, next) =>
{
    var s = ctx.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
    var timeout = s.RequestTimeout > 0 ? s.RequestTimeout : 35;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    cts.CancelAfter(TimeSpan.FromSeconds(timeout));
    ctx.RequestAborted = cts.Token;
    await next();
});

// CORS + security headers + OPTIONS preflight
app.UseDmartResponseHeaders();

// CXB Svelte frontend (embedded resources at /cxb)
app.UseCxb();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "dmart-csharp");

app.MapGroup("/managed").RequireAuthorization().MapManaged();
app.MapGroup("/public").MapPublic();
app.MapGroup("/user").MapUser();
app.MapGroup("/info").RequireAuthorization().MapInfo();
app.MapGroup("/qr").MapQr();

// Load plugins + mount API plugin routes
{
    var pluginManager = app.Services.GetRequiredService<PluginManager>();
    await pluginManager.LoadAsync();
    foreach (var apiPlugin in pluginManager.ActiveApiPlugins)
    {
        var group = app.MapGroup($"/{apiPlugin.Shortname}").RequireAuthorization();
        apiPlugin.MapRoutes(group);
    }
}

app.Run();

// Exposed so dmart.Tests can use WebApplicationFactory<Program>.
public partial class Program;
