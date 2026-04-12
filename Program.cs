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

// dmart Python uses `timestamp without time zone` columns. Npgsql 6+ rejects
// DateTime values with Kind=Utc against those columns unless this switch is set.
// This MUST run before any Npgsql operation, so it lives at the very top.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateSlimBuilder(args);

// AOT-friendly JSON: source-generated context. We REPLACE the default resolver
// (instead of inserting at position 0) so the framework's camelCase default policy
// can't override the snake_case policy declared on DmartJsonContext.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolver = DmartJsonContext.Default;
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    // dmart Python uses response_model_exclude_none=True on every endpoint, so
    // null fields (error on success, attachments when absent, etc.) must not
    // appear on the wire. We have to set this on the runtime SerializerOptions
    // in addition to the attribute on DmartJsonContext — the attribute drives
    // the generated TypeInfo, but the framework pipes requests through a new
    // SerializerOptions instance where the default is "always emit nulls".
    o.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// dmart-Python-style config.env support. We look for config.env in the order
// Python's utils/settings.py uses ($BACKEND_ENV → ./config.env → ~/.dmart/config.env)
// and merge the loaded key=value pairs into IConfiguration under the Dmart:
// section. Loaded BEFORE EnvironmentVariables so actual env vars still override
// dotenv entries — matching pydantic-settings' resolution order.
{
    var (envPath, values) = DotEnv.Load();
    if (envPath is not null && values.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(values);
        builder.Configuration.AddEnvironmentVariables();
    }

    // Wire LISTENING_HOST/LISTENING_PORT from config.env into Kestrel's
    // actual binding, so the binary listens on the configured port without
    // requiring a separate ASPNETCORE_URLS env var. Only applied when
    // ASPNETCORE_URLS isn't already set (explicit env var wins).
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

// Plugins. Every built-in plugin registers itself under its interface so
// PluginManager can match shortnames from config.json to concrete instances.
// Hook plugins (IHookPlugin) get before/after dispatch; API plugins (IApiPlugin)
// mount their own route group at /{shortname}. Users can disable individual
// plugins by flipping is_active in plugins/<name>/config.json — there's no need
// to recompile to remove one from the pipeline.
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

// GZip compression — mirrors Python's GZipMiddleware(minimum_size=10000).
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
});

var app = builder.Build();

// Catches unhandled exceptions from any handler and maps them to a Response.Fail 500.
// Inline so we don't depend on UseMiddleware<T> reflection (AOT-friendly). Must be
// first in the pipeline so it covers auth + endpoint exceptions.
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

// GZip response compression (matches Python's GZipMiddleware).
app.UseResponseCompression();

// Correlation ID header — mirrors Python's CorrelationIdMiddleware.
// If the request has X-Correlation-ID, pass it through; otherwise generate one.
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Headers.ContainsKey("X-Correlation-ID"))
        ctx.Response.Headers["X-Correlation-ID"] = Guid.NewGuid().ToString("N");
    else
        ctx.Response.Headers["X-Correlation-ID"] = ctx.Request.Headers["X-Correlation-ID"].ToString();
    await next();
});

// Request timeout — mirrors Python's asyncio.wait_for(request_timeout).
app.Use(async (ctx, next) =>
{
    var settings = ctx.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
    var timeout = settings.RequestTimeout > 0 ? settings.RequestTimeout : 35;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    cts.CancelAfter(TimeSpan.FromSeconds(timeout));
    ctx.RequestAborted = cts.Token;
    await next();
});

// Dynamic CORS + security headers + OPTIONS preflight handling. Mirrors dmart
// Python's set_middleware_response_headers. Must run after the exception
// handler (so error responses still carry CORS + cache headers) but before
// authentication so OPTIONS preflight short-circuits don't require a JWT.
app.UseDmartResponseHeaders();

// CXB Svelte frontend — served from embedded resources at /cxb.
// Must come before auth so static assets don't require JWT.
app.UseCxb();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "dmart-csharp");

app.MapGroup("/managed").RequireAuthorization().MapManaged();
app.MapGroup("/public").MapPublic();
app.MapGroup("/user").MapUser();
app.MapGroup("/info").RequireAuthorization().MapInfo();
app.MapGroup("/qr").MapQr();

// Load plugin configs from {BaseDir}/plugins/<name>/config.json and mount API
// plugin routes under /{shortname}. Done after the builtin route groups so
// plugin routes can't accidentally shadow a core endpoint, and before app.Run()
// because routing registration has to be complete before the request pipeline
// starts. API plugin routes get RequireAuthorization() to match the Python
// router's capture_body + auth posture.
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
