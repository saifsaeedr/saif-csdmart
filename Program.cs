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
using Dmart;
using Dmart.Api;
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

// Parse subcommand from args.
var subcommand = "serve";
var serverArgs = args;
if (args.Length > 0)
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
    case "-v":
    case "--version":
    {
        // Prints build/git info as colorized formatted JSON.
        string json;
        var infoPath = Path.Combine(AppContext.BaseDirectory, "info.json");
        if (File.Exists(infoPath))
        {
            json = File.ReadAllText(infoPath);
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
            json = $"{{\"branch\":\"{branch}\",\"commit\":\"{commit}\",\"tag\":\"{tag}\",\"runtime\":\".NET {Environment.Version}\"}}";
        }
        PrintColorJson(System.Text.Json.JsonDocument.Parse(json).RootElement, 0);
        Console.WriteLine();
        return;
    }

    case "help":
    case "--help":
    case "-h":
        Console.WriteLine("""
            dmart — Structured CMS/IMS (C# port)

            Usage: dmart [subcommand] [options]

            Subcommands:
              serve          Start the HTTP server (default)
              version        Print version and build info
              settings       Print effective settings as JSON
              set_password   Set password for a user interactively
              check          Run health checks on a space
              export         Export space data to a zip file
              import         Import data from a zip file or folder
              init           Initialize ~/.dmart with config files
              cli            Interactive CLI client (REPL/command/script)
              help           Print this help

            Config file lookup: $BACKEND_ENV → ./config.env → ~/.dmart/config.env
            """);
        return;

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
        if (rows > 0)
        {
            Console.WriteLine($"Password updated for {username}");
            // Update ~/.dmart/cli.ini so dmart-cli picks up the new password
            UpdateCliIni(username, password, s);
        }
        else
        {
            Console.WriteLine($"User {username} not found");
        }
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
        // Initialize ~/.dmart directory with all config files
        var dmartHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dmart");
        Directory.CreateDirectory(dmartHome);
        Console.WriteLine($"Initialized {dmartHome}/");

        // 1. config.env — dmart server configuration
        var configEnvPath = Path.Combine(dmartHome, "config.env");
        if (!File.Exists(configEnvPath))
        {
            var sampleConfig = Path.Combine(AppContext.BaseDirectory, "config.env.sample");
            if (File.Exists(sampleConfig))
                File.Copy(sampleConfig, configEnvPath);
            else
                File.WriteAllText(configEnvPath, """
                    # dmart server configuration
                    # See config.env.sample for all available settings
                    APP_NAME="dmart"
                    APP_URL="http://127.0.0.1:5099"
                    LISTENING_HOST="0.0.0.0"
                    LISTENING_PORT=5099
                    DATABASE_HOST="localhost"
                    DATABASE_PORT=5432
                    DATABASE_USERNAME="dmart"
                    DATABASE_PASSWORD="somepassword"
                    DATABASE_NAME="dmart"
                    JWT_SECRET="change-me-change-me-change-me-32b-minimum-length"
                    ADMIN_SHORTNAME="dmart"
                    ADMIN_PASSWORD="change-me-on-first-login"

                    """.Replace("                    ", ""));
            Console.WriteLine($"  Created {configEnvPath}");
        }
        else
            Console.WriteLine($"  {configEnvPath} already exists");

        // 2. config.json — CXB frontend configuration
        var configJsonPath = Path.Combine(dmartHome, "config.json");
        if (!File.Exists(configJsonPath))
        {
            File.WriteAllText(configJsonPath, """
                {
                  "title": "DMART Unified Data Platform",
                  "footer": "dmart.cc unified data platform",
                  "short_name": "dmart",
                  "display_name": "dmart",
                  "description": "dmart unified data platform",
                  "default_language": "en",
                  "languages": { "ar": "العربية", "en": "English" },
                  "backend": "http://localhost:5099",
                  "websocket": "ws://localhost:5099/ws"
                }

                """.Replace("                ", ""));
            Console.WriteLine($"  Created {configJsonPath}");
        }
        else
            Console.WriteLine($"  {configJsonPath} already exists");

        // 3. cli.ini — dmart-cli configuration
        var cliIniPath = Path.Combine(dmartHome, "cli.ini");
        if (!File.Exists(cliIniPath))
        {
            File.WriteAllText(cliIniPath, """
                # dmart-cli configuration
                url=http://localhost:5099
                shortname=dmart
                password=dmart
                default_space=management
                query_limit=50
                pagination=50

                """.Replace("                ", ""));
            Console.WriteLine($"  Created {cliIniPath}");
        }
        else
            Console.WriteLine($"  {cliIniPath} already exists");

        return;
    }

    case "cli":
    {
        // Interactive CLI client — port of Python cli/cli.py.
        // Usage:
        //   dmart cli                          # REPL mode
        //   dmart cli c <space> <command...>    # Single command
        //   dmart cli s <script_file>           # Script mode
        var exitCode = await Dmart.Cli.CliRunner.RunAsync(serverArgs);
        Environment.ExitCode = exitCode;
        return;
    }

    case "serve":
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

// All logging config from config.env — no appsettings.json needed.
// LOG_FORMAT: "text" (default, human-readable) or "json" (structured JSON lines)
//             When running under systemd, "json" is recommended — journald captures
//             it natively and you can filter with: journalctl -u dmart -o json
// LOG_FILE: path to log file (empty = stdout only, which goes to journald under systemd)
// LOG_LEVEL: trace/debug/information/warning/error/critical/none
{
    var dmartCfg = builder.Configuration.GetSection("Dmart");
    // Default to "json" under systemd (INVOCATION_ID is set by systemd)
    var isSystemd = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"));
    var defaultFormat = isSystemd ? "json" : "text";
    var logFormat = dmartCfg["LogFormat"] ?? dotenvValues.GetValueOrDefault("Dmart:LogFormat") ?? defaultFormat;
    var logFile = dmartCfg["LogFile"] ?? dotenvValues.GetValueOrDefault("Dmart:LogFile") ?? "";
    var logLevelStr = dmartCfg["LogLevel"] ?? dotenvValues.GetValueOrDefault("Dmart:LogLevel") ?? "information";

    // Set minimum log level from config.env (replaces appsettings.json Logging section).
    if (Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var minLevel))
        builder.Logging.SetMinimumLevel(minLevel);

    // Suppress noisy ASP.NET framework logs (previously in appsettings.json).
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

    if (string.Equals(logFormat, "json", StringComparison.OrdinalIgnoreCase))
    {
        builder.Logging.AddJsonConsole(o =>
        {
            o.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            o.UseUtcTimestamp = true;
        });
    }

    if (!string.IsNullOrEmpty(logFile))
    {
        var logDir = Path.GetDirectoryName(logFile);
        if (!string.IsNullOrEmpty(logDir)) Directory.CreateDirectory(logDir);
        builder.Logging.AddProvider(new FileLoggerProvider(logFile, logFormat));
    }
}

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
builder.Services.AddSingleton<WsConnectionManager>();

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

// Structured per-request logging (after auth so ctx.User is populated).
// Mirrors Python's set_logging() — logs method, path, status, duration, user.
// Output is JSON when LOG_FORMAT=json in config.env.
app.UseRequestLogging();

app.MapGet("/", () => "dmart-csharp");

app.MapGroup("/managed").RequireAuthorization().MapManaged();
app.MapGroup("/public").MapPublic();
app.MapGroup("/user").MapUser();
app.MapGroup("/info").RequireAuthorization().MapInfo();
app.MapGroup("/qr").MapQr();

// WebSocket server — port of dmart/websocket.py.
// /ws?token=<jwt>, /send-message/{user}, /broadcast-to-channels, /ws-info
app.MapWebSocket();

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

// Pre-flight check: is the port available?
{
    var port = int.TryParse(app.Configuration["Dmart:ListeningPort"], out var p) ? p : 5099;
    try
    {
        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
    }
    catch (System.Net.Sockets.SocketException)
    {
        Console.Error.WriteLine($"\u001b[31mError: Port {port} is already in use.\u001b[0m");
        Console.Error.WriteLine($"\u001b[33mAnother dmart instance may be running. Stop it first or change LISTENING_PORT in config.env.\u001b[0m");
        Environment.ExitCode = 1;
        return;
    }
}

app.Run();

// Exposed so dmart.Tests can use WebApplicationFactory<Program>.
public partial class Program
{
    // Updates ~/.dmart/cli.ini with the new password (and url/shortname)
    // so dmart-cli picks up the credentials after set_password.
    static void UpdateCliIni(string shortname, string password, DmartSettings s)
    {
        var dmartHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dmart");
        Directory.CreateDirectory(dmartHome);
        var cliIniPath = Path.Combine(dmartHome, "cli.ini");

        // Read existing values or start fresh
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(cliIniPath))
        {
            foreach (var line in File.ReadAllLines(cliIniPath))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                var eq = t.IndexOf('=');
                if (eq <= 0) continue;
                values[t[..eq].Trim()] = t[(eq + 1)..].Trim().Trim('"').Trim('\'');
            }
        }

        // Update credentials
        values["shortname"] = shortname;
        values["password"] = password;
        // Ensure url is set (use the server's own address)
        if (!values.ContainsKey("url"))
            values["url"] = $"http://{s.ListeningHost}:{s.ListeningPort}";

        // Write back
        var lines = new List<string> { "# dmart-cli configuration (updated by dmart set_password)" };
        foreach (var (k, v) in values)
            lines.Add($"{k}={v}");
        File.WriteAllLines(cliIniPath, lines);
        Console.WriteLine($"Updated {cliIniPath} with credentials for {shortname}");
    }

    // ANSI-colorized JSON output for terminal display.
    // Cyan=keys, Yellow=strings, Magenta=numbers, Green=true, Red=false, Gray=null.
    static void PrintColorJson(System.Text.Json.JsonElement el, int indent)
    {
        var pad = new string(' ', indent * 2);
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                Console.WriteLine("{");
                var props = el.EnumerateObject().ToList();
                for (var i = 0; i < props.Count; i++)
                {
                    Console.Write($"{pad}  \u001b[36m\"{props[i].Name}\"\u001b[0m: ");
                    PrintColorJson(props[i].Value, indent + 1);
                    Console.WriteLine(i < props.Count - 1 ? "," : "");
                }
                Console.Write($"{pad}}}");
                break;
            case System.Text.Json.JsonValueKind.Array:
                var items = el.EnumerateArray().ToList();
                if (items.Count == 0) { Console.Write("[]"); break; }
                Console.WriteLine("[");
                for (var i = 0; i < items.Count; i++)
                {
                    Console.Write($"{pad}  ");
                    PrintColorJson(items[i], indent + 1);
                    Console.WriteLine(i < items.Count - 1 ? "," : "");
                }
                Console.Write($"{pad}]");
                break;
            case System.Text.Json.JsonValueKind.String:
                Console.Write($"\u001b[33m\"{el.GetString()}\"\u001b[0m");
                break;
            case System.Text.Json.JsonValueKind.Number:
                Console.Write($"\u001b[35m{el}\u001b[0m");
                break;
            case System.Text.Json.JsonValueKind.True:
                Console.Write("\u001b[32mtrue\u001b[0m");
                break;
            case System.Text.Json.JsonValueKind.False:
                Console.Write("\u001b[31mfalse\u001b[0m");
                break;
            case System.Text.Json.JsonValueKind.Null:
                Console.Write("\u001b[90mnull\u001b[0m");
                break;
            default:
                Console.Write(el.ToString());
                break;
        }
    }
}
