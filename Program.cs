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

// Load config.env early so non-server subcommands can read DB settings.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
var (dotenvPath, dotenvValues) = DotEnv.Load();

switch (subcommand)
{
    case "version":
    {
        // Python reads tag from info.json if present, else git describe --tags.
        var vInfoPath = Path.Combine(AppContext.BaseDirectory, "info.json");
        if (File.Exists(vInfoPath))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(vInfoPath));
                var tag = doc.RootElement.TryGetProperty("tag", out var t) ? t.GetString() : null;
                var ver = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                Console.WriteLine(!string.IsNullOrEmpty(tag) ? tag : $"dmart-csharp {ver ?? "0.1.0"}");
            }
            catch { Console.WriteLine("dmart-csharp 0.1.0"); }
        }
        else
            Console.WriteLine("dmart-csharp 0.1.0 (AOT)");
    }
        return;

    case "help":
    case "--help":
    case "-h":
        Console.WriteLine("""
            dmart — Structured CMS/IMS (C# port)

            Usage: dmart [subcommand] [options]

            Subcommands:
              serve          Start the HTTP server (default)
              hyper          Alias for serve
              version        Print version info
              info           Print build/git info as JSON
              settings       Print effective settings as JSON
              set_password   Set password for a user interactively
              check          Run health checks on a space
              export         Export space data to a zip file
              import         Import data from a zip file or folder
              help           Print this help

            Config file lookup: $BACKEND_ENV → ./config.env → ~/.dmart/config.env
            """);
        return;

    case "info":
    {
        // Python reads info.json or falls back to git rev-parse.
        var infoPath = Path.Combine(AppContext.BaseDirectory, "info.json");
        if (File.Exists(infoPath))
        {
            Console.WriteLine(File.ReadAllText(infoPath));
        }
        else
        {
            static string Git(string gitArgs)
            {
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git", Arguments = gitArgs,
                        RedirectStandardOutput = true, UseShellExecute = false,
                    });
                    var output = p?.StandardOutput.ReadToEnd().Trim();
                    p?.WaitForExit();
                    return output ?? "";
                }
                catch { return ""; }
            }
            var branch = Git("rev-parse --abbrev-ref HEAD");
            var commit = Git("rev-parse --short HEAD");
            var tag = Git("describe --tags");
            Console.WriteLine($"{{\"branch\":\"{branch}\",\"commit\":\"{commit}\",\"tag\":\"{tag}\",\"runtime\":\".NET {Environment.Version}\"}}");
        }
        return;
    }

    case "settings":
    {
        // Print effective DmartSettings as JSON (mirrors Python's settings.model_dump_json).
        var cfgBuilder = new ConfigurationBuilder();
        cfgBuilder.AddJsonFile("appsettings.json", optional: true);
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        // Print key settings (avoid leaking secrets — mask password/jwt)
        Console.WriteLine($"{{");
        Console.WriteLine($"  \"database_host\": \"{s.DatabaseHost}\",");
        Console.WriteLine($"  \"database_port\": {s.DatabasePort},");
        Console.WriteLine($"  \"database_username\": \"{s.DatabaseUsername}\",");
        Console.WriteLine($"  \"database_name\": \"{s.DatabaseName}\",");
        Console.WriteLine($"  \"listening_host\": \"{s.ListeningHost}\",");
        Console.WriteLine($"  \"listening_port\": {s.ListeningPort},");
        Console.WriteLine($"  \"management_space\": \"{s.ManagementSpace}\",");
        Console.WriteLine($"  \"jwt_issuer\": \"{s.JwtIssuer}\",");
        Console.WriteLine($"  \"jwt_access_minutes\": {s.JwtAccessMinutes},");
        Console.WriteLine($"  \"is_registrable\": {s.IsRegistrable.ToString().ToLower()},");
        Console.WriteLine($"  \"max_failed_login_attempts\": {s.MaxFailedLoginAttempts},");
        Console.WriteLine($"  \"max_sessions_per_user\": {s.MaxSessionsPerUser},");
        Console.WriteLine($"  \"allowed_cors_origins\": \"{s.AllowedCorsOrigins}\",");
        Console.WriteLine($"  \"websocket_url\": \"{s.WebsocketUrl ?? ""}\"");
        Console.WriteLine($"}}");
        return;
    }

    case "set_password":
    {
        // Interactive password reset — mirrors Python's set_admin_passwd.py
        Console.Write("Username: ");
        var username = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(username)) { Console.Error.WriteLine("Username required"); Environment.ExitCode = 1; return; }
        Console.Write("New password: ");
        var password = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(password) || password.Length < 8) { Console.Error.WriteLine("Password must be >= 8 chars"); Environment.ExitCode = 1; return; }

        // Build minimal DI to get Db + UserRepository + PasswordHasher
        var cfgBuilder = new ConfigurationBuilder();
        cfgBuilder.AddJsonFile("appsettings.json", optional: true);
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        var dbInst = new Db(Microsoft.Extensions.Options.Options.Create(s));
        if (!dbInst.IsConfigured) { Console.Error.WriteLine("Database not configured"); Environment.ExitCode = 1; return; }
        var hasher = new PasswordHasher();
        var hashed = hasher.Hash(password);
        await using var conn = await dbInst.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("UPDATE users SET password = $1, is_active = true WHERE shortname = $2", conn);
        cmd.Parameters.Add(new() { Value = hashed });
        cmd.Parameters.Add(new() { Value = username });
        var rows = await cmd.ExecuteNonQueryAsync();
        Console.WriteLine(rows > 0 ? $"Password updated for {username}" : $"User {username} not found");
        return;
    }

    case "check":
    case "health-check":
    {
        // Run health checks — mirrors Python's check subcommand.
        var space = serverArgs.Length > 0 ? serverArgs[0] : null;
        var cfgBuilder = new ConfigurationBuilder();
        cfgBuilder.AddJsonFile("appsettings.json", optional: true);
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        var dbInst = new Db(Microsoft.Extensions.Options.Options.Create(s));
        if (!dbInst.IsConfigured) { Console.Error.WriteLine("Database not configured"); Environment.ExitCode = 1; return; }
        var healthRepo = new HealthCheckRepository(dbInst);
        var spacesToCheck = new List<string>();
        if (!string.IsNullOrEmpty(space))
        {
            spacesToCheck.Add(space);
        }
        else
        {
            // Check all spaces
            var spaceRepo = new SpaceRepository(dbInst);
            var allSpaces = await spaceRepo.ListAsync();
            spacesToCheck.AddRange(allSpaces.Select(sp => sp.Shortname));
        }
        Console.WriteLine("{");
        var first = true;
        foreach (var sp in spacesToCheck)
        {
            if (!first) Console.WriteLine(",");
            first = false;
            Console.Write($"  \"{sp}\": {{");
            foreach (var ht in new[] { "orphan_attachments", "dangling_owner", "stale_locks", "missing_payload_body", "missing_schema_reference" })
            {
                try
                {
                    var results = await healthRepo.RunAsync(sp, ht);
                    Console.Write($"\"{ht}\": {results.Count}");
                    if (ht != "missing_schema_reference") Console.Write(", ");
                }
                catch { Console.Write($"\"{ht}\": -1"); if (ht != "missing_schema_reference") Console.Write(", "); }
            }
            Console.Write("}");
        }
        Console.WriteLine("\n}");
        return;
    }

    case "export":
    {
        // Export space data to zip — mirrors Python's export subcommand.
        var spaceName = serverArgs.FirstOrDefault(a => !a.StartsWith('-'));
        var outputIdx = Array.IndexOf(serverArgs, "--output");
        var output = outputIdx >= 0 && outputIdx + 1 < serverArgs.Length ? serverArgs[outputIdx + 1] : null;
        if (string.IsNullOrEmpty(output)) { Console.Error.WriteLine("Usage: dmart export [space_name] --output file.zip"); Environment.ExitCode = 1; return; }
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) output += ".zip";

        var cfgBuilder = new ConfigurationBuilder();
        cfgBuilder.AddJsonFile("appsettings.json", optional: true);
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        var dbInst = new Db(Microsoft.Extensions.Options.Options.Create(s));
        if (!dbInst.IsConfigured) { Console.Error.WriteLine("Database not configured"); Environment.ExitCode = 1; return; }

        var nlog = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        var refresher = new AuthzCacheRefresher(dbInst, nlog.CreateLogger<AuthzCacheRefresher>());
        var entryRepo = new EntryRepository(dbInst);
        var entryService = new EntryService(entryRepo,
            new AttachmentRepository(dbInst),
            new HistoryRepository(dbInst),
            new PermissionService(new UserRepository(dbInst, refresher),
                new AccessRepository(dbInst, refresher), refresher),
            new PluginManager(Array.Empty<IHookPlugin>(), Array.Empty<IApiPlugin>(),
                new SpaceRepository(dbInst), nlog.CreateLogger<PluginManager>()),
            new SchemaValidator(entryRepo, nlog.CreateLogger<SchemaValidator>()));
        var exportService = new ImportExportService(entryRepo, entryService, nlog.CreateLogger<ImportExportService>());

        var spaceRepo = new SpaceRepository(dbInst);
        var spaces = string.IsNullOrEmpty(spaceName)
            ? (await spaceRepo.ListAsync()).Select(sp => sp.Shortname).ToList()
            : new List<string> { spaceName };

        using var zipStream = File.Create(output);
        using var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var sp in spaces)
        {
            var stream = await exportService.ExportAsync(sp, "/", "dmart");
            stream.Position = 0;
            var entry = zip.CreateEntry($"{sp}.json");
            await using var es = entry.Open();
            await stream.CopyToAsync(es);
        }
        Console.WriteLine($"Exported {spaces.Count} space(s) to {output}");
        return;
    }

    case "import":
    {
        var target = serverArgs.Length > 0 ? serverArgs[0] : ".";
        Console.WriteLine($"Import from: {target}");
        Console.WriteLine("Note: CLI import requires the full migration pipeline (json_to_db).");
        Console.WriteLine("Use the /managed/import HTTP endpoint for zip import instead.");
        return;
    }

    case "init":
    {
        // Initialize ~/.dmart directory with sample config
        var dmartHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dmart");
        Directory.CreateDirectory(dmartHome);
        var sampleConfig = Path.Combine(AppContext.BaseDirectory, "config.env.sample");
        var targetConfig = Path.Combine(dmartHome, "config.env");
        if (File.Exists(sampleConfig) && !File.Exists(targetConfig))
        {
            File.Copy(sampleConfig, targetConfig);
            Console.WriteLine($"Created {targetConfig} from sample");
        }
        else if (File.Exists(targetConfig))
        {
            Console.WriteLine($"{targetConfig} already exists");
        }
        else
        {
            Console.WriteLine($"Initialized {dmartHome}/ (no sample config found to copy)");
        }
        return;
    }

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

var builder = WebApplication.CreateSlimBuilder(serverArgs);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolver = DmartJsonContext.Default;
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// Reuse the dotenv values loaded at the top of Program.cs.
{
    if (dotenvPath is not null && dotenvValues.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(dotenvValues);
        builder.Configuration.AddEnvironmentVariables();
    }

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
