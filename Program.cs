using Dmart.Api.Info;
using Dmart.Api.Managed;
using Dmart.Api.Public;
using Dmart.Api.Qr;
using Dmart.Api.User;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Middleware;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Plugins;
using Dmart.Plugins.BuiltIn;
using Dmart.Plugins.Native;
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


// Top-level exception handler — clean error message, no stack trace, no core dump.
// Disabled when flag-like args are present (WebApplicationFactory injects --contentRoot).
if (args.Length == 0 || !args[0].StartsWith('-'))
{
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        var ex = e.ExceptionObject as Exception;
        Console.Error.WriteLine($"\u001b[31mError: {ex?.Message ?? "unknown error"}\u001b[0m");
        if (ex?.InnerException is not null)
            Console.Error.WriteLine($"\u001b[33m  {ex.InnerException.Message}\u001b[0m");
        Environment.Exit(1);
    };
}

// Parse subcommand from args. Default to "serve".
// Flag-like args (--contentRoot etc.) are left for the web builder;
// only our known flags (-v, -h) are treated as subcommands.
var subcommand = "serve";
var serverArgs = args;
if (args.Length > 0)
{
    var first = args[0].ToLowerInvariant();
    if (first is "-v" or "--version" or "-h" or "--help")
    {
        subcommand = first;
        serverArgs = args[1..];
    }
    else if (!first.StartsWith('-'))
    {
        subcommand = first;
        serverArgs = args[1..];
    }
}

// Load config.env early so non-server subcommands can read DB settings.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
var (dotenvPath, dotenvValues) = DotEnv.Load();

// Strict config.env validation — refuse to start on unknown keys so typos
// (DATABAE_HOST vs DATABASE_HOST) and stale keys from renamed/removed settings
// don't silently fall through to defaults. Mirrors pydantic-settings'
// `extra = "forbid"` behavior.
if (dotenvPath is not null)
{
    var rawKeys = DotEnv.Parse(dotenvPath);
    var keyErrors = DotEnvStrictCheck.ValidateKeys(dotenvPath, rawKeys);
    if (keyErrors.Count > 0)
    {
        Console.Error.WriteLine("Error: config.env contains unrecognized keys:");
        foreach (var err in keyErrors) Console.Error.WriteLine($"  - {err}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Fix or remove these keys and retry. See config.env.sample for the valid set.");
        Environment.Exit(1);
    }
}

switch (subcommand)
{
    case "version":
    case "-v":
    case "--version":
    {
        // Version info is baked into the binary via InformationalVersion at build time.
        // Format: "tag branch=X date=Y" — set by build.sh -p:InformationalVersion=...
        // Falls back to info.json or live git for development (dotnet run).
        string json;
        var asmVersion = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrEmpty(asmVersion) && asmVersion.Contains("branch="))
        {
            // Parse "tag branch=X date=Y" format
            var parts = asmVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tag = parts[0];
            var branch = parts.FirstOrDefault(p => p.StartsWith("branch="))?[7..] ?? "";
            var date = string.Join(' ', parts.Where(p => p.StartsWith("date=")).Select(p => p[5..]));
            if (string.IsNullOrEmpty(date))
                date = parts.SkipWhile(p => !p.StartsWith("date=")).Skip(1).FirstOrDefault() ?? "";
            var commit = tag.Contains('-') ? tag.Split('-').LastOrDefault()?.TrimStart('g') ?? "" : tag;
            json = $"{{\"branch\":\"{branch}\",\"version\":\"{commit}\",\"tag\":\"{tag}\",\"version_date\":\"{date}\",\"runtime\":\".NET {Environment.Version}\"}}";
        }
        else
        {
            // No baked-in version — development build via dotnet run
            json = $"{{\"version\":\"dev\",\"runtime\":\".NET {Environment.Version}\"}}";
        }
        PrintColorJson(System.Text.Json.JsonDocument.Parse(json).RootElement, 0);
        Console.WriteLine();
        return;
    }

    case "help":
    case "--help":
    case "-h":
        Console.WriteLine("""
            dmart — Structured CMS / IMS

            Usage: dmart [subcommand] [options]

            Subcommands:
              serve          Start the HTTP server
                             Options: --cxb-config <path>
              migrate        Create/update the PG schema (idempotent; no server)
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
        // Print effective DmartSettings as colorized JSON. Shares its
        // projection with GET /info/settings via SettingsSerializer so CLI
        // and API output stay in sync. Secrets are redacted automatically.
        var cfgBuilder = new ConfigurationBuilder();
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        var dict = SettingsSerializer.ToPublicDictionary(s);

        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            foreach (var (k, v) in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                switch (v)
                {
                    case bool b:   w.WriteBoolean(k, b); break;
                    case int i:    w.WriteNumber(k, i); break;
                    case long l:   w.WriteNumber(k, l); break;
                    case double d: w.WriteNumber(k, d); break;
                    case System.Collections.IList list:
                        w.WriteStartArray(k);
                        foreach (var item in list) w.WriteStringValue(item?.ToString() ?? "");
                        w.WriteEndArray();
                        break;
                    default: w.WriteString(k, v?.ToString() ?? ""); break;
                }
            }
            w.WriteEndObject();
        }
        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        PrintColorJson(System.Text.Json.JsonDocument.Parse(json).RootElement, 0);
        Console.WriteLine();
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
        await using var cmd = new Npgsql.NpgsqlCommand(
            "UPDATE users SET password = $1, is_active = true, attempt_count = 0 WHERE shortname = $2", conn);
        cmd.Parameters.Add(new() { Value = hashed });
        cmd.Parameters.Add(new() { Value = username });
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows > 0)
        {
            Console.WriteLine($"Password updated for {username} (account unlocked: is_active=true, attempt_count=0)");
            // Only update ~/.dmart/cli.ini when its existing shortname matches
            // the user whose password we just reset — avoids clobbering a
            // CLI operator's own credentials when resetting someone else.
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
            new SchemaValidator(entryRepo, nlog.CreateLogger<SchemaValidator>()),
            new WorkflowEngine(entryRepo, nlog.CreateLogger<WorkflowEngine>()),
            nlog.CreateLogger<EntryService>());
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
        var zipPath = serverArgs.FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrEmpty(zipPath))
        {
            Console.Error.WriteLine("Usage: dmart import <zip-file>");
            Environment.ExitCode = 1;
            return;
        }
        if (!File.Exists(zipPath))
        {
            Console.Error.WriteLine($"File not found: {zipPath}");
            Environment.ExitCode = 1;
            return;
        }

        var cfgBuilder = new ConfigurationBuilder();
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
            new SchemaValidator(entryRepo, nlog.CreateLogger<SchemaValidator>()),
            new WorkflowEngine(entryRepo, nlog.CreateLogger<WorkflowEngine>()),
            nlog.CreateLogger<EntryService>());
        var importService = new ImportExportService(entryRepo, entryService, nlog.CreateLogger<ImportExportService>());

        await using var zipStream = File.OpenRead(zipPath);
        // Actor for imported entries — "dmart" is the hardcoded admin shortname
        // (same as AdminBootstrap uses); matches the export subcommand above.
        var resp = await importService.ImportZipAsync(zipStream, "dmart");
        var inserted = resp.Attributes?.GetValueOrDefault("inserted") ?? 0;
        var failedCount = resp.Attributes?.GetValueOrDefault("failed_count") ?? 0;
        Console.WriteLine($"Imported {inserted} entries from {zipPath} ({failedCount} failed)");
        Environment.ExitCode = (failedCount is int fc && fc > 0) ? 2 : 0;
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
            var jwtSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            if (File.Exists(sampleConfig))
            {
                // Copy sample and replace the placeholder JWT secret with a random one
                var content = File.ReadAllText(sampleConfig);
                content = content.Replace("change-me-change-me-change-me-32b-minimum-length", jwtSecret);
                File.WriteAllText(configEnvPath, content);
            }
            else
                File.WriteAllText(configEnvPath,
                    "# dmart server configuration\n" +
                    "LISTENING_HOST=\"0.0.0.0\"\n" +
                    "LISTENING_PORT=5099\n" +
                    "DATABASE_HOST=\"localhost\"\n" +
                    "DATABASE_PORT=5432\n" +
                    "DATABASE_USERNAME=\"dmart\"\n" +
                    "DATABASE_PASSWORD=\"somepassword\"\n" +
                    "DATABASE_NAME=\"dmart\"\n" +
                    $"JWT_SECRET=\"{jwtSecret}\"\n" +
                    "# Admin user 'dmart' is created passwordless. Set password via: dmart set_password\n");
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

    case "migrate":
    {
        // Schema migration — creates/updates the PG schema without starting
        // the HTTP server. Equivalent to Python's `alembic upgrade head`.
        // Runs SqlSchema.CreateAll which contains:
        //   * CREATE TABLE IF NOT EXISTS (new tables)
        //   * ALTER TABLE ... ADD COLUMN IF NOT EXISTS (new columns)
        //   * CREATE INDEX IF NOT EXISTS (new indexes)
        //   * CREATE MATERIALIZED VIEW IF NOT EXISTS (authz views)
        // Idempotent — safe to re-run. Captures PG NOTICE messages so the
        // caller sees exactly what was created vs. skipped.
        //
        // Options:
        //   -q, --quiet    Suppress per-statement output (show summary only)
        var quiet = serverArgs.Contains("-q") || serverArgs.Contains("--quiet");

        var cfgBuilder = new ConfigurationBuilder();
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        var dbInst = new Db(Microsoft.Extensions.Options.Options.Create(s));
        if (!dbInst.IsConfigured)
        {
            Console.Error.WriteLine("Error: Database not configured. Set DATABASE_HOST/PORT/USERNAME/PASSWORD/NAME in config.env.");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            Console.WriteLine($"Migrating {s.DatabaseName}@{s.DatabaseHost}:{s.DatabasePort} ...");

            await using var conn = await dbInst.OpenAsync();
            // PG emits NOTICE when `IF NOT EXISTS` guards short-circuit — capture
            // those so the operator sees what was applied vs skipped. The sink
            // also counts "actually ran" events (no NOTICE = a real CREATE/ALTER).
            var notices = new List<string>();
            conn.Notice += (_, e) =>
            {
                notices.Add(e.Notice.MessageText);
                if (!quiet) Console.WriteLine($"  [pg] {e.Notice.Severity}: {e.Notice.MessageText}");
            };

            // Advisory lock 1 — same lock SchemaInitializer uses, so running
            // `migrate` alongside a live server is serialized at PG level.
            await using (var lk = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(1)", conn))
                await lk.ExecuteNonQueryAsync();
            try
            {
                await using var cmd = new Npgsql.NpgsqlCommand(SqlSchema.CreateAll, conn);
                await cmd.ExecuteNonQueryAsync();
                await conn.ReloadTypesAsync();

                // Second pass: detect columns that the C# models expect but the
                // live DB is missing. CreateAll's static ALTER list only covers
                // historical additions — this picks up anything newer that the
                // maintainer forgot to add to the forward-compat block.
                var applied = await ApplyExpectedColumnPatches(conn, quiet);

                // Summary: "actually applied" = real CREATE/ALTER (no NOTICE
                // saying "already exists"). NOTICE count is a rough proxy for
                // "skipped" when IF NOT EXISTS guards fired.
                var skipped = notices.Count(n =>
                    n.Contains("already exists", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"dmart schema ready. {applied} dynamic column patches applied, {skipped} statements already-in-sync.");
            }
            finally
            {
                await using var ul = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock(1)", conn);
                await ul.ExecuteNonQueryAsync();
            }
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: migration failed: {ex.Message}");
            if (ex.InnerException is not null)
                Console.Error.WriteLine($"  {ex.InnerException.Message}");
            Environment.ExitCode = 1;
            return;
        }
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
// SERVER STARTUP (subcommand: serve)
// ============================================================================

// Parse serve options before passing remaining args to the web builder.
// --cxb-config <path>  — path to CXB config.json
for (var i = 0; i < serverArgs.Length - 1; i++)
{
    if (serverArgs[i] is "--cxb-config")
    {
        Environment.SetEnvironmentVariable("DMART_CXB_CONFIG", serverArgs[i + 1]);
        serverArgs = serverArgs[..i].Concat(serverArgs[(i + 2)..]).ToArray();
        break;
    }
}

var builder = WebApplication.CreateSlimBuilder(serverArgs);
builder.Services.AddOpenApi(options =>
{
    // Make the generated openapi.json describe endpoints relative to its
    // own URL rather than to the server's origin. With `servers: [{ url: ".." }]`,
    // a Swagger UI loaded at https://host/<anyprefix>/docs/ will resolve the
    // spec from https://host/<anyprefix>/docs/openapi.json and route "Try it
    // out" calls back through https://host/<anyprefix>/<endpoint>. Works
    // whether dmart is mounted at /, /dmart/, /abc/xyz/, or anywhere else
    // behind a reverse proxy — no path-base awareness needed on the server.
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Servers = new List<Microsoft.OpenApi.OpenApiServer>
        {
            new() { Url = ".." },
        };
        return Task.CompletedTask;
    });
});

// All logging config from config.env.
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

    // When LOG_FILE is set, Python's ljson.log is unconditionally JSON. Match
    // that: force json output regardless of LOG_FORMAT so the file is always
    // a clean stream of JSON lines.
    if (!string.IsNullOrEmpty(logFile))
        logFormat = "json";

    // Set minimum log level from config.env.
    if (Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var minLevel))
        builder.Logging.SetMinimumLevel(minLevel);

    // Suppress noisy ASP.NET framework logs.
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

    // LogSink is always registered so RequestLoggingMiddleware can resolve
    // it from DI unconditionally; it internally no-ops when LogFile is
    // empty. When LogFile IS set, hook it up as an ILoggerProvider too so
    // every generic ILogger event lands in the same JSONL file.
    builder.Services.AddSingleton<LogSink>();
    if (!string.IsNullOrEmpty(logFile))
    {
        builder.Logging.Services.AddSingleton<ILoggerProvider>(sp =>
            new FileLoggerProvider(sp.GetRequiredService<LogSink>()));
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
builder.Services.AddOptions<DmartSettings>()
    .Bind(builder.Configuration.GetSection("Dmart"))
    .Services.AddSingleton<IValidateOptions<DmartSettings>, DmartSettingsValidator>();
// Force validation at startup — otherwise misconfig only surfaces on first access.
builder.Services.AddOptions<DmartSettings>().ValidateOnStart();
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
builder.Services.AddSingleton<InvitationRepository>();
builder.Services.AddSingleton<CountHistoryRepository>();
builder.Services.AddSingleton<HealthCheckRepository>();
builder.Services.AddSingleton<SpaceRepository>();
builder.Services.AddHostedService<CountHistorySnapshotter>();

// Schema bootstrapper runs once on startup. AdminBootstrap MUST be registered AFTER
// SchemaInitializer — IHostedServices run StartAsync sequentially in registration order.
builder.Services.AddHostedService<SchemaInitializer>();
builder.Services.AddHostedService<AdminBootstrap>();

// IP-based rate limiter for authentication endpoints. Account lockout (on the
// user row) limits attempts per-account; this limits attempts per-IP so an
// attacker can't enumerate accounts by slowly cycling shortnames at the
// per-account threshold. 10 req/min/IP is a safe default — a legitimate user
// retyping a password hits this much rarer than a scripted attack.
builder.Services.AddRateLimiter(opts =>
{
    opts.AddPolicy("auth-by-ip", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    // Rejected requests get HTTP 429 with a short JSON body matching our
    // Response.Fail shape, not the default empty 429.
    opts.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync(
            "{\"status\":\"failed\",\"error\":{\"type\":\"rate_limit\",\"code\":429,\"message\":\"too many requests\"}}",
            ct);
    };
});

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
builder.Services.AddSingleton<InvitationJwt>();
builder.Services.AddSingleton<SmsSender>();
builder.Services.AddSingleton<SmtpSender>();
builder.Services.AddSingleton<InvitationService>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<OtpProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.GoogleProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.FacebookProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.AppleProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.OAuthUserResolver>();
builder.Services.AddSingleton<Dmart.Auth.OAuthCodeStore>();
builder.Services.AddSingleton<Dmart.Auth.OAuthClientStore>();
builder.Services.AddDmartAuth(builder.Configuration);

// Plugins
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IHookPlugin, ResourceFoldersCreationPlugin>();
builder.Services.AddSingleton<IHookPlugin, RealtimeUpdatesNotifierPlugin>();
builder.Services.AddSingleton<IHookPlugin, AuditPlugin>();
builder.Services.AddSingleton<IHookPlugin, AdminNotificationSenderPlugin>();
builder.Services.AddSingleton<IHookPlugin, SystemNotificationSenderPlugin>();
builder.Services.AddSingleton<IHookPlugin, LocalNotificationPlugin>();
builder.Services.AddSingleton<IHookPlugin, SemanticIndexerPlugin>();
builder.Services.AddSingleton<IHookPlugin, McpSseBridgePlugin>();
builder.Services.AddSingleton<Dmart.Api.Mcp.McpSessionStore>();
builder.Services.AddSingleton<EmbeddingProvider>();
builder.Services.AddSingleton<SemanticSearchService>();
builder.Services.AddSingleton<SemanticIndexerService>();
builder.Services.AddSingleton<IApiPlugin, DbSizeInfoPlugin>();

// Native .so plugins from ~/.dmart/plugins/ — no rebuild needed.
builder.Services.AddNativePlugins();

// Per-request context
builder.Services.AddScoped<RequestContext>();

// GZip compression
builder.Services.AddResponseCompression(o => { o.EnableForHttps = true; });

// Global request body size limit (50 MB)
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 50 * 1024 * 1024);
// Kestrel's body limit applies to raw POST bodies; multipart form-data has a
// separate limit (128 MB by default) that can otherwise bypass the 50 MB cap.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 50 * 1024 * 1024;
    o.ValueLengthLimit = 50 * 1024 * 1024;
});

var app = builder.Build();

// Register the shutdown hook for subprocess plugins so each one gets a
// clean stdin-close (EOF) when dmart starts shutting down. Paired with the
// SDK sample's SIGINT handling, this silences the KeyboardInterrupt trace
// that terminal Ctrl+C would otherwise elicit.
Dmart.Plugins.Native.NativePluginLoader.WireSubprocessShutdown(
    app.Services.GetRequiredService<IHostApplicationLifetime>());

// Log the baked-in build version as the first line of the startup banner.
// Uses Microsoft.Hosting.Lifetime so it appears alongside Kestrel's
// "Now listening on" in the standard info stream and is captured by the
// JSONL sink (LOG_FILE) as a structured {Version, Runtime} record.
{
    var v = typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "dev";
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Microsoft.Hosting.Lifetime")
        .LogInformation("dmart {Version} on .NET {Runtime}", v, Environment.Version);
}

// Exception handler
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        var cid = ctx.Response.Headers["X-Correlation-ID"].ToString();
        var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ExceptionHandler");
        logger?.LogError(ex, "Unhandled exception cid={Cid}", cid);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json";
            var body = Dmart.Models.Api.Response.Fail(
                Dmart.Models.Api.InternalErrorCode.SOMETHING_WRONG,
                $"An internal error occurred. Reference: {cid}", ErrorTypes.Exception);
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

// Python parity: unmatched API routes return INVALID_ROUTE (230) under
// type=request with HTTP 422, instead of an empty 404 body. Registered BEFORE
// UseCxb so its post-next pass runs AFTER CxbMiddleware's SPA fallback has had
// a chance to turn /cxb/* 404s into index.html. Skip the CXB URL prefix so
// deployments without a built-in CXB bundle (CI runners, minimal containers)
// still return a plain 404 for /cxb/* — transforming it would trip the
// `if (resp.StatusCode == NotFound) return;` skip branches in the CXB tests.
{
    var dmartSettings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DmartSettings>>().Value;
    static PathString NormalizedPrefix(string? raw, string fallback)
    {
        var p = (raw?.Trim().TrimEnd('/') ?? fallback);
        if (!p.StartsWith('/')) p = "/" + p;
        return new PathString(p);
    }
    var cxbPath = NormalizedPrefix(dmartSettings.CxbUrl, "/cxb");
    var catPath = NormalizedPrefix(dmartSettings.CatUrl, "/cat");

    app.Use(async (ctx, next) =>
    {
        await next();
        if (ctx.Response.StatusCode == 404
            && !ctx.Response.HasStarted
            && (ctx.Response.ContentLength is null or 0)
            && !ctx.Request.Path.StartsWithSegments(cxbPath)
            && !ctx.Request.Path.StartsWithSegments(catPath))
        {
            var body = Dmart.Models.Api.Response.Fail(
                Dmart.Models.Api.InternalErrorCode.INVALID_ROUTE,
                $"Route not found: {ctx.Request.Method} {ctx.Request.Path}",
                ErrorTypes.Request);
            ctx.Response.StatusCode = 422;
            await ctx.Response.WriteAsJsonAsync(body, Dmart.Models.Json.DmartJsonContext.Default.Response);
        }
    });
}

// Embedded SPAs (CXB at /cxb, Catalog at /cat by default).
app.UseCxb();
app.UseCatalog();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Structured per-request logging (after auth so ctx.User is populated).
// Mirrors Python's set_logging() — logs method, path, status, duration, user.
// Output is JSON when LOG_FORMAT=json in config.env.
app.UseRequestLogging();

app.MapOpenApi("/docs/openapi.json");
app.MapGet("/docs", () => Results.Content("""
<html>
<head>
    <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist/swagger-ui.css" />
</head>
<body>
    <div id="swagger-ui"></div>
    <script src="https://unpkg.com/swagger-ui-dist/swagger-ui-bundle.js"></script>
    <script>
        SwaggerUIBundle({ url: 'openapi.json', dom_id: '#swagger-ui' });
    </script>
</body>
</html>
""", "text/html")).ExcludeFromDescription();
app.MapGet("/", () => Results.Content("{\"status\":\"success\",\"message\":\"DMART-CS API\"}", "application/json")).WithTags("Root");

app.MapGroup("/managed").WithTags("Managed").RequireAuthorization().AddEndpointFilter<FailedResponseFilter>().MapManaged();
app.MapGroup("/public").WithTags("Public").AddEndpointFilter<FailedResponseFilter>().MapPublic();
app.MapGroup("/user").WithTags("User").AddEndpointFilter<FailedResponseFilter>().MapUser();
app.MapGroup("/info").WithTags("Info").RequireAuthorization().AddEndpointFilter<FailedResponseFilter>().MapInfo();
app.MapGroup("/qr").WithTags("QR").AddEndpointFilter<FailedResponseFilter>().MapQr();

// OAuth 2.1 Authorization Server for MCP clients. Discovery + DCR + the
// authorize/token endpoints live alongside the JWT-protected /mcp route so a
// single host provides everything an MCP client needs to onboard with
// zero-config (just the base URL).
Dmart.Api.Oauth.OAuthEndpoints.MapOAuth(app);

// Model Context Protocol — hand-rolled, AOT-safe. Routes: POST/GET/DELETE /mcp.
// Auth is applied per-route inside MapMcp via RequireAuthorization() — the
// caller's JWT flows through to tool handlers so permissions are enforced.
Dmart.Api.Mcp.McpEndpoint.MapMcp(app);

// WebSocket server — port of dmart/websocket.py.
// /ws?token=<jwt>, /send-message/{user}, /broadcast-to-channels, /ws-info
app.MapWebSocket();

// Load plugins + mount API plugin routes
{
    var pluginManager = app.Services.GetRequiredService<PluginManager>();
    await pluginManager.LoadAsync();
    foreach (var apiPlugin in pluginManager.ActiveApiPlugins)
    {
        var group = app.MapGroup($"/{apiPlugin.Shortname}").WithTags("Plugins").RequireAuthorization().AddEndpointFilter<FailedResponseFilter>();
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
    // Columns the C# code expects to read/write on each table. Compared against
    // information_schema.columns during `dmart migrate` — any that are missing
    // get an ALTER TABLE ADD COLUMN issued dynamically. This is the fallback
    // path for schema drift beyond what SqlSchema.CreateAll's static forward-
    // compat block covers. Shape: table → [(column, ddl_type_and_constraints)].
    private static readonly Dictionary<string, (string Column, string Ddl)[]> ExpectedColumns = new()
    {
        ["users"] = new[]
        {
            ("device_id", "TEXT"),
            ("google_id", "TEXT"),
            ("facebook_id", "TEXT"),
            ("social_avatar_url", "TEXT"),
            ("attempt_count", "INTEGER"),
            ("last_login", "JSONB"),
            ("notes", "TEXT"),
            ("locked_to_device", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        },
        ["roles"] = new[]
        {
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        },
        ["permissions"] = new[]
        {
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        },
        ["entries"] = new[]
        {
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
        },
        ["spaces"] = new[]
        {
            ("last_checksum_history", "TEXT"),
            ("query_policies", "TEXT[] NOT NULL DEFAULT '{}'"),
            ("active_plugins", "JSONB"),
            ("hide_folders", "JSONB"),
            ("hide_space", "BOOLEAN"),
            ("ordinal", "INTEGER"),
            ("mirrors", "JSONB"),
        },
        ["sessions"] = new[]
        {
            ("firebase_token", "TEXT"),
        },
    };

    // Compares each table's live columns to ExpectedColumns and issues
    // ALTER TABLE ADD COLUMN for any missing entries. Returns the count of
    // ALTERs actually issued. Skips tables that don't exist (they'll be
    // created by SqlSchema.CreateAll in the same pass).
    static async Task<int> ApplyExpectedColumnPatches(Npgsql.NpgsqlConnection conn, bool quiet)
    {
        var applied = 0;
        foreach (var (table, cols) in ExpectedColumns)
        {
            // Skip tables that don't exist yet — CreateAll creates them fully
            // with every column, so there's nothing to patch.
            await using (var check = new Npgsql.NpgsqlCommand(
                "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = $1", conn))
            {
                check.Parameters.Add(new() { Value = table });
                if (await check.ExecuteScalarAsync() is null) continue;
            }

            // Load the live column set once per table.
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var q = new Npgsql.NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = $1", conn))
            {
                q.Parameters.Add(new() { Value = table });
                await using var r = await q.ExecuteReaderAsync();
                while (await r.ReadAsync()) existing.Add(r.GetString(0));
            }

            foreach (var (column, ddl) in cols)
            {
                if (existing.Contains(column)) continue;
                var sql = $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {ddl}";
                await using var alter = new Npgsql.NpgsqlCommand(sql, conn);
                await alter.ExecuteNonQueryAsync();
                applied++;
                if (!quiet) Console.WriteLine($"  + {table}.{column} ({ddl})");
            }
        }
        return applied;
    }

    // Updates ~/.dmart/cli.ini with the new password, but ONLY if the file
    // already exists and its `shortname=` matches the user being reset.
    // Rationale: an admin resetting another user's password shouldn't
    // overwrite their own dmart-cli credentials. A missing file or missing
    // `shortname` is treated as "no match" — we leave it alone.
    static void UpdateCliIni(string shortname, string password, DmartSettings s)
    {
        var dmartHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dmart");
        var cliIniPath = Path.Combine(dmartHome, "cli.ini");

        if (!File.Exists(cliIniPath))
        {
            Console.WriteLine($"Skipped {cliIniPath} (file does not exist)");
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(cliIniPath))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            values[t[..eq].Trim()] = t[(eq + 1)..].Trim().Trim('"').Trim('\'');
        }

        if (!values.TryGetValue("shortname", out var existing)
            || !string.Equals(existing, shortname, StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"Skipped {cliIniPath} (shortname='{existing ?? ""}' does not match '{shortname}')");
            return;
        }

        values["password"] = password;
        if (!values.ContainsKey("url"))
            values["url"] = $"http://{s.ListeningHost}:{s.ListeningPort}";

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
