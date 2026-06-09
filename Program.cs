using Dmart.Api.Info;
using Dmart.Api.Managed;
using Dmart.Api.Public;
using Dmart.Api.Qr;
using Dmart.Api.User;
using Dmart.Auth;
using Dmart.Cli;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Middleware;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
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
//   dmart help        Print available subcommands (default if no subcommand)
//   dmart serve       Start the HTTP server
//   dmart version     Print version info
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

// Parse subcommand from args.
// - No args at all → "help" (terminal user intent: show subcommands).
// - Flag-like args (--contentRoot etc., as injected by WebApplicationFactory)
//   → "serve" so the test host actually starts.
// - Bare subcommand (`dmart serve`, `dmart migrate`, ...) → that subcommand.
// - `-v`/`-h`/`--version`/`--help` are also treated as subcommands.
var subcommand = args.Length == 0 ? "help" : "serve";
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

// Bulk-update query_policies for a chunk of rows in one of the five
// ACL-filterable tables (entries, users, roles, permissions, spaces). Builds
// one `UPDATE … FROM (VALUES …)` statement that joins the target table
// against a literal VALUES list keyed on (shortname, space_name, subpath) —
// the same primary key the per-row UPDATEs in fix_query_policies and
// update_query_policies used to use, just shipped in one statement. Caller
// is responsible for chunking (Postgres caps prepared-statement parameters
// at 65535; 4 params per row → cap well over our 1000-row chunk size).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: tableName is from a hardcoded set in each caller (entries/users/roles/permissions/spaces for fix_query_policies, 'entries' for update_query_policies). The dynamic VALUES list embeds only integer placeholder indices; all caller-supplied values flow through NpgsqlCommand.Parameters.")]
static async Task<int> BulkUpdatePoliciesAsync(
    Npgsql.NpgsqlConnection conn,
    string tableName,
    List<(string Shortname, string SpaceName, string Subpath, string[] Policies)> rows,
    int offset, int len)
{
    if (len == 0) return 0;

    var sb = new System.Text.StringBuilder(120 + 100 * len);
    sb.Append("UPDATE ").Append(tableName).Append(" AS t SET query_policies = v.policies FROM (VALUES");
    for (var i = 0; i < len; i++)
    {
        if (i > 0) sb.Append(',');
        // 4 params per row, starting at $1. Cast the first row's columns so
        // Postgres can infer the VALUES column types; later rows inherit.
        var p = 1 + i * 4;
        sb.Append('(').Append('$').Append(p).Append("::text,$")
                      .Append(p + 1).Append("::text,$")
                      .Append(p + 2).Append("::text,$")
                      .Append(p + 3).Append("::text[])");
    }
    sb.Append(") AS v(shortname, space_name, subpath, policies) WHERE t.shortname = v.shortname AND t.space_name = v.space_name AND t.subpath = v.subpath");

    await using var cmd = new Npgsql.NpgsqlCommand(sb.ToString(), conn);
    for (var i = 0; i < len; i++)
    {
        var row = rows[offset + i];
        cmd.Parameters.Add(new() { Value = row.Shortname });
        cmd.Parameters.Add(new() { Value = row.SpaceName });
        cmd.Parameters.Add(new() { Value = row.Subpath });
        cmd.Parameters.Add(new()
        {
            Value = row.Policies,
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        });
    }
    return await cmd.ExecuteNonQueryAsync();
}

switch (subcommand)
{
    case "version":
    case "-v":
    case "--version":
    {
        // Version info is baked into the binary via InformationalVersion at build time.
        // Format: "describe branch=X date=Y" — set by build.sh -p:InformationalVersion=...
        // `describe` is the raw `git describe --tags --always --long` output:
        // always "<tag>-<n>-g<sha>" (e.g. "v0.8.22-0-g6c220d2" on the tag,
        // "v0.8.22-3-g1a2b3c4" three commits past). The short SHA is always
        // embedded, so there's no separate commit field.
        string json;
        var asmVersion = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrEmpty(asmVersion) && asmVersion.Contains("branch="))
        {
            var parts = asmVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var version = parts[0];
            var branch = parts.FirstOrDefault(p => p.StartsWith("branch="))?[7..] ?? "";
            // VERSION_DATE from build.sh is "YYYY-MM-DD HH:MM:SS +ZONE" — the
            // space-split already isolates the date component as the first
            // date= token; later time/zone tokens are discarded on purpose.
            var date = parts.FirstOrDefault(p => p.StartsWith("date="))?[5..] ?? "";
            json = $"{{\"version\":\"{version}\",\"branch\":\"{branch}\",\"version_date\":\"{date}\",\"runtime\":\".NET {Environment.Version}\"}}";
        }
        else
        {
            // No baked-in version — development build via dotnet run
            json = $"{{\"version\":\"dev\",\"runtime\":\".NET {Environment.Version}\"}}";
        }
        CliConsole.PrintColorJson(System.Text.Json.JsonDocument.Parse(json).RootElement, 0);
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
              passwd         Set password for a user. Shortname can be passed
                             positionally: `dmart passwd <shortname>` skips
                             the shortname prompt. `dmart passwd` with no
                             args prompts for both shortname and password.
              check          Run health checks on a space
              selfcheck      Smoke-test the running HTTP surface (login + CRUD + query)
                             Usage: dmart selfcheck [--url <url>] [--admin <name>]
                                                    [--password <pwd> | --password-stdin]
                                                    [--space <name>] [--keep] [-v]
              preflight      Scan a legacy filesystem export for integrity issues
                             (duplicate UUIDs / missing owners / schema-noncompliant
                             payloads) and auto-fix them before `dmart import`.
                             Usage: dmart preflight [--dry-run] [--workers N]
                                                    [--output-dir D] <spaces-folder>
              import         Load a zip or folder export into the database. Supports
                             --fast, --fast-parallelism=N, --batch-size=N, --resume
                             (filesystem only — sidecar checkpoint at
                             <source>/.dmart-import-checkpoint.json lets a crash
                             resume from the last committed pass / space).
              export         Export a space to a zip in the dmart on-disk layout
                             Usage: dmart export <space_name> [--output <path|dir|.>]
                             --output unset      → ./<space>.zip
                             --output .          → ./<space>.zip
                             --output some/dir/  → some/dir/<space>.zip
                             --output snap.zip   → snap.zip
              import         Import data from a zip file or folder
                             By default existing rows are skipped (idempotent).
                             Pass `-r` (or `--replace`) to overwrite them.
              init           Initialize ~/.dmart with config files
              seed           Seed bundled sample spaces and/or populate the DB
                             Usage: dmart seed [files-only | db-only] [--force]
                             (no arg)    → files-only + db-only
                             files-only  → copy bundled spaces to SpacesFolder
                                           (fallback ~/.dmart/spaces)
                             db-only     → import SpacesFolder (or fallback)
                                           into the database
                             --force     → overwrite existing files / upsert
                                           existing rows (default: skip both)
              fix_query_policies
                             Backfill entries.query_policies for rows written
                             before write-time population landed. Idempotent.
                             Args: [<space>] [--dry-run]
              update_query_policies
                             Recompute query_policies for every entry and
                             update rows whose stored value drifted (e.g.
                             owner / is_active changed without going through
                             the write path). Mirrors Python's
                             update_query_policies.py.
                             Args: [--batch-size <N>] (default 1000)
              create-users-folders
                             Backfill personal/people/<shortname>/{notifications,
                             private,protected,public,inbox} for every user.
                             Idempotent — existing folders are left untouched.
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
        CliConsole.PrintColorJson(System.Text.Json.JsonDocument.Parse(json).RootElement, 0);
        Console.WriteLine();
        return;
    }

    case "passwd":
    {
        // Mirrors Python's set_admin_passwd.py. Shortname can be supplied
        // positionally (`dmart passwd dmart`) so install-time scripting
        // doesn't have to feed it through stdin; falls back to the
        // interactive prompt when omitted.
        //
        // Note: we intentionally do NOT accept the password as a
        // second positional arg. Passwords on a command line leak into
        // shell history, `ps`, and process-listing audits — read it
        // through Console.ReadLine instead.
        string? username = serverArgs.Length >= 1 ? serverArgs[0].Trim() : null;
        if (string.IsNullOrEmpty(username))
        {
            Console.Write("Username: ");
            username = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrEmpty(username)) { Console.Error.WriteLine("Username required"); Environment.ExitCode = 1; return; }
        Console.Write("New password: ");
        var password = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(password) || password.Length < 8) { Console.Error.WriteLine("Password must be >= 8 chars"); Environment.ExitCode = 1; return; }

        // Build minimal DI to get Db + UserRepository + PasswordHasher
        var (s, dbInst) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues);
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
            // Deliberately NOT auto-rewriting ~/.dmart/cli.ini. Storing the
            // plaintext password in a file every time it rotates is a
            // footgun (cleartext on disk, file-perms drift on each write).
            // Operator updates cli.ini manually if they want persistent
            // CLI auth, or sets DMART_PASSWORD per shell session.
            Console.WriteLine(
                "Note: ~/.dmart/cli.ini was not modified. Update it manually if you " +
                "need persistent CLI auth, or set DMART_PASSWORD in your shell.");
        }
        else
        {
            Console.WriteLine($"User {username} not found");
        }
        return;
    }

    case "selfcheck":
    {
        // Operator-facing HTTP smoke. Talks to a running dmart over its
        // REST surface; doesn't touch the DB itself, so no CliBootstrap.
        // Defaults pull LISTENING_PORT / ADMIN_SHORTNAME / ADMIN_PASSWORD
        // out of the already-loaded config.env (dotenvValues), matching
        // curl.sh's env-then-config-then-fallback resolution order.
        Environment.ExitCode = await Dmart.Cli.SelfCheckCommand.Run(serverArgs, dotenvValues, dotenvPath);
        return;
    }

    case "preflight":
    {
        // Filesystem integrity scanner + auto-fixer for large legacy
        // migrations. Pure file I/O — no DB, no live server. See
        // Cli/PreflightCommand.cs for the full description of the
        // three scanners (UUID dedup / owner fixup / schema violation
        // flagging).
        Environment.ExitCode = await Dmart.Cli.PreflightCommand.Run(serverArgs);
        return;
    }

    case "check":
    case "health-check":
    {
        // Run health checks — mirrors Python's check subcommand.
        var space = serverArgs.Length > 0 ? serverArgs[0] : null;
        var (_, dbInst) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues);
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
        // Mirrors POST /managed/export: one Query → one zip in the standard
        // dmart on-disk layout. The previous CLI shape (zip-of-zips with
        // `.json` extensions) was a port artifact; the API path always
        // produced one flat archive and tooling expects that.
        var spaceName = serverArgs.FirstOrDefault(a => !a.StartsWith('-'));
        var outputIdx = Array.IndexOf(serverArgs, "--output");
        var output = outputIdx >= 0 && outputIdx + 1 < serverArgs.Length ? serverArgs[outputIdx + 1] : null;
        if (string.IsNullOrEmpty(spaceName))
        {
            Console.Error.WriteLine("Usage: dmart export <space_name> [--output <path|dir|.>]");
            Environment.ExitCode = 1;
            return;
        }

        // Resolve --output:
        //   * unset            → ./<space>.zip
        //   * "." / existing dir / trailing slash → <dir>/<space>.zip
        //   * anything else    → use as-is, append .zip if missing
        string outputPath;
        if (string.IsNullOrEmpty(output))
        {
            outputPath = Path.GetFullPath($"{spaceName}.zip");
        }
        else if (output == "." || output == ".."
                 || output.EndsWith('/') || output.EndsWith(Path.DirectorySeparatorChar)
                 || Directory.Exists(output))
        {
            outputPath = Path.GetFullPath(Path.Combine(output, $"{spaceName}.zip"));
        }
        else
        {
            outputPath = output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? output : output + ".zip";
            outputPath = Path.GetFullPath(outputPath);
        }

        var (s, dbInst) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues);
        var exportService = CliBootstrap.BuildImportExportService(s, dbInst);

        // Same call path as the HTTP /managed/export handler — but with
        // actor: null so the row-level ACL filter is skipped. The API gate
        // exists to scope an HTTP caller to the rows their JWT can see; a
        // CLI run on the server has full DB access already, so filtering by
        // the dmart user's policies would silently drop rows the operator
        // expects in the archive (this was the "shortnames not honored"
        // symptom — the ACL gate excluded entries whose stored
        // query_policies didn't intersect with the dmart user's, so those
        // shortnames never landed in the zip).
        await using var exportStream = await exportService.ExportAsync(spaceName, "/", actor: null);
        await using var fileStream = File.Create(outputPath);
        await exportStream.CopyToAsync(fileStream);
        Console.WriteLine($"Exported space '{spaceName}' to {outputPath}");
        return;
    }

    case "import":
    {
        // Local helper: write the error to stderr, set the process exit
        // code to 1, and return — callers do `Bail("..."); return;` to
        // collapse the four-line "WriteLine + ExitCode + return" pattern
        // into two lines per error site.
        static void Bail(string msg)
        {
            Console.Error.WriteLine(msg);
            Environment.ExitCode = 1;
        }

        var targetPath = serverArgs.FirstOrDefault(a => !a.StartsWith('-'));
        // -r / --replace flips the default patch behavior: existing rows
        // get their non-key columns rewritten from the source's meta. Without
        // -r the import is idempotent — pre-existing rows are skipped and
        // the operator sees them counted as `skipped` in the summary.
        var replace = serverArgs.Any(a => a is "-r" or "--replace");
        // --fast bypasses FK constraints AND user-defined triggers for the
        // entire import by setting session_replication_role='replica' on a
        // single shared session. Trades safety for speed; only safe when
        // the source is internally consistent (typical for operator-trusted
        // CLI imports). Hard-fails if the DB role lacks the privilege.
        var fast = serverArgs.Any(a => a is "--fast");
        // --fast-parallelism=N splits Pass 3-5 (entries/attachments/history)
        // across N parallel per-space workers, each on its own connection
        // with its own transaction. Drops the single-tx-for-the-whole-import
        // property: on crash some spaces fully landed, others not — operator
        // re-runs with -r/--replace. Only honored when --fast is set.
        var parallelism = 1;
        var parallelArg = serverArgs.FirstOrDefault(a => a.StartsWith("--fast-parallelism=", StringComparison.Ordinal));
        if (parallelArg is not null)
        {
            if (!int.TryParse(parallelArg["--fast-parallelism=".Length..], out parallelism) || parallelism < 1 || parallelism > 16)
            {
                Bail("--fast-parallelism must be an integer in [1, 16]");
                return;
            }
            if (!fast)
            {
                Bail("--fast-parallelism requires --fast (it has no effect on the slow path)");
                return;
            }
        }
        // --batch-size=N caps the in-memory bulk-COPY accumulator. Lower
        // it for fat payloads (e.g. base64-encoded blobs in payload.body)
        // where 10k rows × payload would exceed the host RAM budget; raise
        // it for tiny payloads where the COPY round-trip overhead matters.
        // Only meaningful with --fast (slow path doesn't accumulate).
        var batchSize = Dmart.Services.ImportExportService.DefaultBatchSize;
        var batchArg = serverArgs.FirstOrDefault(a => a.StartsWith("--batch-size=", StringComparison.Ordinal));
        if (batchArg is not null)
        {
            if (!int.TryParse(batchArg["--batch-size=".Length..], out batchSize) || batchSize < 1 || batchSize > 1_000_000)
            {
                Bail("--batch-size must be an integer in [1, 1000000]");
                return;
            }
        }
        // --space=NAME --subpath=PATH together turn on "remap mode" — the
        // source folder is treated as content to drop INTO the named
        // existing space at the given parent subpath, instead of being a
        // full space dump where {space}/ sits at the top. Both flags
        // required together, fs source only.
        var targetSpace = serverArgs.FirstOrDefault(a => a.StartsWith("--space=", StringComparison.Ordinal))?
            ["--space=".Length..];
        var targetSubpath = serverArgs.FirstOrDefault(a => a.StartsWith("--subpath=", StringComparison.Ordinal))?
            ["--subpath=".Length..];
        if ((targetSpace is null) != (targetSubpath is null))
        {
            Bail("--space and --subpath must be provided together (or neither)");
            return;
        }
        // --resume picks up from a sidecar checkpoint left behind by a
        // previous run that crashed mid-import. Filesystem-only — the
        // checkpoint file lives at <source>/.dmart-import-checkpoint.json
        // by default (overridable with --checkpoint-file). Skips the head
        // pass entirely if it already committed, and skips per-space
        // tails for spaces that already finished. Most useful with
        // --fast --fast-parallelism>1 where each space has its own
        // transaction boundary.
        var resume = serverArgs.Any(a => a is "--resume");
        var checkpointPath = serverArgs.FirstOrDefault(a => a.StartsWith("--checkpoint-file=", StringComparison.Ordinal))?
            ["--checkpoint-file=".Length..];

        // --from-list=FILE: import straight from a saved work-list (one
        // source-relative meta path per line) and SKIP the filesystem walk.
        // The list is trusted as-is — useful to resume a huge import without
        // re-enumerating a slow/remote tree, or to feed a list built by
        // external preflight tooling. fs-only.
        var fromListPath = serverArgs.FirstOrDefault(a => a.StartsWith("--from-list=", StringComparison.Ordinal))?
            ["--from-list=".Length..];
        // --save-list=FILE: write the enumerated work-list here instead of the
        // default <source>/.dmart-import-worklist.txt. (The list is written on
        // every walk regardless; this only overrides the path.)
        var saveListPath = serverArgs.FirstOrDefault(a => a.StartsWith("--save-list=", StringComparison.Ordinal))?
            ["--save-list=".Length..];
        // --spaces=a,b,c: import only the named top-level spaces from a
        // spaces-root. The import path must be the PARENT that holds {space}/…
        // dirs (the first path segment is the space name); entries outside the
        // set are skipped during the walk. fs-only; mutually exclusive with the
        // --space/--subpath single-space remap.
        var includeSpaces = serverArgs.FirstOrDefault(a => a.StartsWith("--spaces=", StringComparison.Ordinal))?
            ["--spaces=".Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // --tag=VALUE: stamp an arbitrary tag onto every imported entry.
        // Repeatable (pass --tag=a --tag=b for multiple). Deduped against
        // each entry's existing tags. Typical use: mark a migration batch
        // (e.g. --tag=migrated-2026-05) so the imported rows can be found,
        // filtered, or rolled back later.
        var importTags = serverArgs
            .Where(a => a.StartsWith("--tag=", StringComparison.Ordinal))
            .Select(a => a["--tag=".Length..])
            .Where(t => t.Length > 0)
            .ToList();

        // --skip-history: don't import history.jsonl files (Pass 5). History
        // is an audit trail, not current state — often unwanted in a migration
        // target, frequently carries sensitive diffs (password changes), and
        // is a large fraction of on-disk bytes. Dropping it speeds the import
        // and sidesteps data-quality issues that cluster in history.
        var skipHistory = serverArgs.Any(a => a is "--skip-history");

        // --no-validate: disable the always-on import-time validation
        // (owner remap, uuid dedup, schema validation, parse-error logging).
        // Default is validate=true; this flag flips it off — useful when
        // the operator has already run preflight separately and doesn't
        // want the per-entry overhead repeated, or when the sidecar file
        // is undesirable.
        var noValidate = serverArgs.Any(a => a is "--no-validate");

        // --issues-file=PATH: override the default sidecar location
        // (default is <source>/import-issues-<timestamp>.jsonl).
        var issuesFilePath = serverArgs.FirstOrDefault(a => a.StartsWith("--issues-file=", StringComparison.Ordinal))?
            ["--issues-file=".Length..];

        // --since=ISO-DATE: drop entries whose file mtime is older than the
        // cutoff. Uses mtime as a proxy for the meta's updated_at (matches
        // in practice because dmart Python writes the meta on every update;
        // breaks if rsync clobbered the mtimes). Combines naturally with
        // --filter-subpath. One stat() per surviving path, no file read.
        DateTime? sinceUtc = null;
        var sinceArg = serverArgs.FirstOrDefault(a => a.StartsWith("--since=", StringComparison.Ordinal))?
            ["--since=".Length..];
        if (sinceArg is not null)
        {
            if (!DateTime.TryParse(sinceArg, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                Bail($"--since: cannot parse '{sinceArg}' as a date (try ISO-8601, e.g. 2026-02-26T00:00:00Z)");
                return;
            }
            sinceUtc = parsed;
        }
        // --type={zip|fs} selects the source kind. Default is auto-detected
        // from the target: a regular file ⇒ zip, a directory ⇒ fs. When
        // explicitly set and the user's choice disagrees with the target's
        // actual shape, we warn (and prompt for confirmation in interactive
        // mode) before proceeding — the explicit --type wins after that, so
        // the impossible case (`--type=zip` on a folder, or vice-versa) is
        // pre-validated and hard-fails with a clean message rather than a
        // raw IO exception.
        string? explicitType = null;
        var typeArg = serverArgs.FirstOrDefault(a => a.StartsWith("--type=", StringComparison.Ordinal));
        if (typeArg is not null)
        {
            explicitType = typeArg["--type=".Length..];
            if (explicitType is not "zip" and not "fs")
            {
                Bail("--type must be 'zip' or 'fs'");
                return;
            }
        }
        if (string.IsNullOrEmpty(targetPath))
        {
            Bail("Usage: dmart import [-r|--replace] [--fast] [--fast-parallelism=N] [--batch-size=N] [--type=zip|fs] [--space=NAME --subpath=PATH] [--since=ISO-DATE] [--skip-history] [--tag=VALUE] [--no-validate] [--issues-file=PATH] [--resume] [--checkpoint-file=PATH] [--from-list=FILE] [--save-list=FILE] [--spaces=A,B,C] <path>");
            return;
        }

        var targetIsFile = File.Exists(targetPath);
        var targetIsDir = Directory.Exists(targetPath);
        if (!targetIsFile && !targetIsDir)
        {
            Bail($"Path not found: {targetPath}");
            return;
        }
        var detectedType = targetIsDir ? "fs" : "zip";
        var effectiveType = explicitType ?? detectedType;

        if (explicitType is not null && explicitType != detectedType)
        {
            if (!Console.IsInputRedirected)
            {
                Console.Write($"Warning: --type={explicitType} but target appears to be {detectedType}. Continue with --type={explicitType}? [y/N] ");
                var line = Console.ReadLine();
                if (!string.Equals(line?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    Bail("Aborted.");
                    return;
                }
            }
            else
            {
                Console.Error.WriteLine($"Warning: --type={explicitType} but target appears to be {detectedType}. Proceeding with --type={explicitType}.");
            }
        }

        // Pre-validate the chosen type against the target shape so we emit a
        // clean error instead of letting File.OpenRead(dir) / EnumerateFiles(file)
        // throw a raw IO exception further down.
        if (effectiveType == "zip" && !targetIsFile)
        {
            Bail($"Error: --type=zip requires a regular file, but '{targetPath}' is not a regular file.");
            return;
        }
        if (effectiveType == "fs" && !targetIsDir)
        {
            Bail($"Error: --type=fs requires a directory, but '{targetPath}' is not a directory.");
            return;
        }
        if (effectiveType == "zip" && resume)
        {
            Bail("--resume requires --type=fs (zip resume is not supported; the sidecar checkpoint needs a stable on-disk location next to the source folder)");
            return;
        }
        if (effectiveType == "zip" && (fromListPath is not null || saveListPath is not null))
        {
            Bail("--from-list / --save-list require --type=fs (work-lists are relative to a source folder; zip imports read every archive member)");
            return;
        }
        if (effectiveType == "zip" && includeSpaces is { Length: > 0 })
        {
            Bail("--spaces requires --type=fs");
            return;
        }
        if (includeSpaces is { Length: > 0 } && (targetSpace is not null || targetSubpath is not null))
        {
            Bail("--spaces (filter a multi-space import) can't combine with --space/--subpath (remap into one space) — they're opposite modes");
            return;
        }

        var remapSuffix = targetSpace is not null ? $", into-space={targetSpace}, into-subpath={targetSubpath}" : "";
        var sinceSuffix = sinceUtc.HasValue ? $", since={sinceUtc.Value:o}" : "";
        Console.WriteLine($"Importing from {targetPath} (type={effectiveType}, replace={replace}, fast={fast}, parallelism={parallelism}, batch-size={batchSize}{remapSuffix}{sinceSuffix})");

        var (s, dbInst) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues);
        var importService = CliBootstrap.BuildImportExportService(s, dbInst);

        // The actor argument is accepted for API stability but unused — every
        // imported record's owner comes from its meta's owner_shortname, with
        // a literal "dmart" backstop when missing (set inside the service).
        // CLI default: preserveExisting=true (skip rows that already exist).
        // With -r/--replace the caller opts back into upsert-everything,
        // matching the HTTP /managed/import handler.
        Response resp;
        if (effectiveType == "fs")
        {
            resp = await importService.ImportFolderAsync(targetPath, actor: null,
                preserveExisting: !replace, fastUnsafeNoFkCheck: fast, fastParallelism: parallelism,
                batchSize: batchSize, targetSpace: targetSpace, targetSubpath: targetSubpath,
                resume: resume, checkpointPath: checkpointPath,
                sinceUtc: sinceUtc,
                validate: !noValidate, issuesFilePath: issuesFilePath,
                skipHistory: skipHistory,
                importTags: importTags.Count > 0 ? importTags : null,
                fromListPath: fromListPath, saveListPath: saveListPath,
                includeSpaces: includeSpaces);
        }
        else
        {
            if (targetSpace is not null || targetSubpath is not null)
            {
                Bail("--space / --subpath remap mode requires --type=fs (zip remap is not supported)");
                return;
            }
            await using var zipStream = File.OpenRead(targetPath);
            resp = await importService.ImportZipAsync(zipStream, actor: null,
                preserveExisting: !replace, fastUnsafeNoFkCheck: fast, fastParallelism: parallelism, batchSize: batchSize);
        }

        if (resp.Status != Status.Success)
        {
            Console.Error.WriteLine($"Import failed: {resp.Error?.Message ?? "unknown error"}");
            Environment.ExitCode = 1;
            return;
        }

        // Sum the per-kind insert counts the service returns. The previous
        // CLI only read a non-existent `inserted` key, so it always printed
        // "Imported 0 entries" even when thousands of rows landed.
        static int Read(Response r, string key)
            => r.Attributes is { } a && a.TryGetValue(key, out var v) && v is int i ? i : 0;
        var entries_inserted = Read(resp, "entries_inserted");
        var attachments_inserted = Read(resp, "attachments_inserted");
        var spaces_inserted = Read(resp, "spaces_inserted");
        var users_inserted = Read(resp, "users_inserted");
        var roles_inserted = Read(resp, "roles_inserted");
        var permissions_inserted = Read(resp, "permissions_inserted");
        var histories_inserted = Read(resp, "histories_inserted");
        var skipped = Read(resp, "skipped");
        var failed_count = Read(resp, "failed_count");
        var totalInserted = entries_inserted + attachments_inserted + spaces_inserted
                          + users_inserted + roles_inserted + permissions_inserted + histories_inserted;

        Console.WriteLine($"Imported {totalInserted} rows from {targetPath} (skipped {skipped} existing, {failed_count} failed)");
        Console.WriteLine($"  entries={entries_inserted} attachments={attachments_inserted} spaces={spaces_inserted}"
            + $" users={users_inserted} roles={roles_inserted} permissions={permissions_inserted}"
            + $" histories={histories_inserted}");

        // When any entries failed, dump the per-record details next to the
        // source as JSON Lines. One {"path":..., "kind":..., "error":...}
        // record per failure so it pipes cleanly through `jq` /
        // `grep -E '"kind":"entry"'` for triage. For zip the log lands beside
        // the file; for fs it lands beside the source folder (uses the
        // folder's parent dir and the folder's name as the basename).
        if (failed_count > 0
            && resp.Attributes?.GetValueOrDefault("failed") is List<Dictionary<string, object>> failedList)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var baseName = effectiveType == "fs"
                ? Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : Path.GetFileNameWithoutExtension(targetPath);
            var baseDir = effectiveType == "fs"
                ? (Path.GetDirectoryName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? ".")
                : (Path.GetDirectoryName(targetPath) ?? ".");
            var logPath = Path.GetFullPath(
                Path.Combine(baseDir, $"{baseName}.import-failures-{stamp}.jsonl"));
            await using var sw = new StreamWriter(logPath, append: false);
            foreach (var f in failedList)
                await sw.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(f,
                    Dmart.Models.Json.DmartJsonContext.Default.DictionaryStringObject));
            Console.WriteLine($"Failure details: {logPath}");
        }

        Environment.ExitCode = failed_count > 0 ? 2 : 0;
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
                    "# Admin user 'dmart' is created passwordless. Set password via: dmart passwd\n");
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

        // 3. cli.ini — dmart-cli configuration. Written via CliIniWriter
        // so the file lands with 0600 perms on Unix; the password is on
        // disk in cleartext, so the file is only readable by the owner.
        var cliIniPath = Path.Combine(dmartHome, "cli.ini");
        if (!File.Exists(cliIniPath))
        {
            CliIniWriter.WriteSecure(cliIniPath, """
                # dmart-cli configuration
                url=http://localhost:5099
                shortname=dmart
                password=dmart
                default_space=management
                query_limit=50
                pagination=50

                """.Replace("                ", ""));
            Console.WriteLine($"  Created {cliIniPath} (perms 0600)");
        }
        else
            Console.WriteLine($"  {cliIniPath} already exists");

        return;
    }

    case "seed":
    {
        // Lay down sample spaces and/or populate the DB from an on-disk
        // spaces tree. Three modes:
        //   (default)     files-only + db-only
        //   files-only    copy bundled sample spaces → SpacesFolder
        //                 (fallback ~/.dmart/spaces)
        //   db-only       zip SpacesFolder (or fallback) in memory and
        //                 import into the DB via ImportZipAsync
        //
        // The two halves live in SeedCommand so this case stays a thin
        // orchestrator and each half is independently testable.
        var modeArg = serverArgs.FirstOrDefault(a => !a.StartsWith('-'));
        if (modeArg is not null && modeArg != "files-only" && modeArg != "db-only")
        {
            Console.Error.WriteLine($"Unknown seed mode '{modeArg}'. Use 'files-only', 'db-only', or omit for both.");
            Environment.ExitCode = 1;
            return;
        }
        var doFiles = modeArg is null or "files-only";
        var doDb = modeArg is null or "db-only";
        // --force flips both halves from idempotent-skip to overwrite:
        //   files: clobber existing files in SpacesFolder
        //   db:    upsert (replace non-key columns) instead of skipping
        // Mirrors `dmart import -r/--replace` for the DB half.
        var force = serverArgs.Any(a => a is "--force" or "-f");

        // Bind settings to resolve SpacesFolder. DB isn't required for
        // files-only, so skip CliBootstrap until we actually need it.
        var seedCfgBuilder = new ConfigurationBuilder();
        if (dotenvPath is not null) seedCfgBuilder.AddInMemoryCollection(dotenvValues);
        seedCfgBuilder.AddEnvironmentVariables();
        var seedCfg = seedCfgBuilder.Build();
        var seedSettings = new DmartSettings();
        seedCfg.GetSection("Dmart").Bind(seedSettings);
        var spacesFolder = !string.IsNullOrWhiteSpace(seedSettings.SpacesFolder)
            ? seedSettings.SpacesFolder
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dmart", "spaces");
        spacesFolder = Path.GetFullPath(spacesFolder);

        if (doFiles)
        {
            var rc = SeedCommand.SeedFiles(spacesFolder, force);
            if (rc != 0) { Environment.ExitCode = rc; return; }
        }

        if (doDb)
        {
            Environment.ExitCode = await SeedCommand.SeedDbAsync(spacesFolder, dotenvPath, dotenvValues, force);
        }
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

        var (s, dbInst) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues,
            "Error: Database not configured. Set DATABASE_HOST/PORT/USERNAME/PASSWORD/NAME in config.env.");

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
                // No client-side timeout. Some statements in CreateAll (notably the
                // TIMESTAMPTZ→TIMESTAMP migration) rewrite every row in large tables
                // and exceed Npgsql's default 30s on production-sized DBs.
                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync();
                await conn.ReloadTypesAsync();

                // Second pass: detect columns that the C# models expect but the
                // live DB is missing. CreateAll's static ALTER list only covers
                // historical additions — this picks up anything newer that the
                // maintainer forgot to add to the forward-compat block.
                var applied = await ExpectedColumnPatcher.ApplyAsync(conn, quiet);

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

    case "fix_query_policies":
    case "fix-query-policies":
    {
        // Backfill query_policies for rows written by code paths that
        // predated write-time population. Covers all five tables with
        // ACL-filterable rows: entries, users, roles, permissions, spaces.
        // Rows with an empty TEXT[] are invisible to AppendAclFilter (the
        // row-level ACL intersects the caller's policy list against the
        // row array via LIKE; empty never matches).
        //
        // Usage:
        //   dmart fix_query_policies              → backfill orphans in every table
        //   dmart fix_query_policies <space>      → restrict to one space
        //   dmart fix_query_policies --dry-run    → count + sample only, don't UPDATE
        //   dmart fix_query_policies <space> --dry-run
        //
        // Idempotent: rows with a non-empty query_policies array are skipped
        // at the SQL level. Safe to run on a live DB; each UPDATE touches
        // only the query_policies column.
        string? spaceFilter = null;
        var dryRun = false;
        foreach (var a in serverArgs)
        {
            if (a == "--dry-run") dryRun = true;
            else if (!a.StartsWith('-')) spaceFilter = a;
        }

        var (s, dbInst) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues,
            "Error: Database not configured. Set DATABASE_* in config.env.");

        Console.WriteLine($"{(dryRun ? "[dry-run] " : "")}Scanning {s.DatabaseName}@{s.DatabaseHost}:{s.DatabasePort} for rows with empty query_policies"
            + (spaceFilter is null ? " across all spaces..." : $" in space '{spaceFilter}'..."));

        await using var conn = await dbInst.OpenAsync();

        // Each table has the same shape for the columns we read: shortname,
        // space_name, subpath, resource_type, is_active, owner_shortname,
        // owner_group_shortname. Only the `entries` table is special-cased
        // on resource_type = 'folder' to set entryShortname (the rest of
        // the tables' rows are never folders).
        var tables = new[] { "entries", "users", "roles", "permissions", "spaces" };
        var grandTotal = 0;

        foreach (var tableName in tables)
        {
            var selectSql = $"""
                SELECT shortname, space_name, subpath, resource_type, is_active,
                       owner_shortname, owner_group_shortname
                FROM {tableName}
                WHERE COALESCE(array_length(query_policies, 1), 0) = 0
                """ + (spaceFilter is null ? "" : "\n  AND space_name = $1") + """

                ORDER BY space_name, subpath, shortname
                """;
#pragma warning disable CA2100 // Audited: selectSql is composed of constants + the loop variable `tableName` which iterates the hardcoded `tables` array; spaceFilter binds via $1.
            await using var sel = new Npgsql.NpgsqlCommand(selectSql, conn);
#pragma warning restore CA2100
            if (spaceFilter is not null)
                sel.Parameters.Add(new() { Value = spaceFilter });

            var orphans = new List<(string shortname, string spaceName, string subpath,
                string resourceType, bool isActive, string owner, string? ownerGroup)>();
            await using (var r = await sel.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    orphans.Add((
                        r.GetString(0), r.GetString(1), r.GetString(2),
                        r.GetString(3), r.GetBoolean(4),
                        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
                }
            }

            if (orphans.Count == 0)
            {
                Console.WriteLine($"  {tableName}: 0 orphans.");
                continue;
            }

            Console.WriteLine($"  {tableName}: {orphans.Count} orphan(s).");

            if (dryRun)
            {
                foreach (var (sn, sp, subp, rt, _, _, _) in orphans.Take(5))
                    Console.WriteLine($"    {sp}:{subp}/{sn} ({rt})");
                if (orphans.Count > 5) Console.WriteLine($"    ... +{orphans.Count - 5} more");
                continue;
            }

            // Pre-compute every orphan's policies in C# (same QueryPolicies.Generate
            // call as before, just gathered up front), then ship in chunks via a
            // single UPDATE ... FROM (VALUES …) per chunk. Collapses N round-trips
            // to ⌈N/CHUNK_SIZE⌉ without changing what the rows end up looking like.
            var rows = new List<(string Shortname, string SpaceName, string Subpath, string[] Policies)>(orphans.Count);
            foreach (var (sn, sp, subp, rt, act, own, og) in orphans)
            {
                // Only entries.resource_type == 'folder' wants entryShortname
                // populated (mirrors Python's generate_query_policies special
                // case). Users/roles/permissions/spaces never set it.
                var entryShortname = (tableName == "entries" && rt == "folder") ? sn : null;
                var policies = Dmart.Utils.QueryPolicies.Generate(
                    spaceName: sp, subpath: subp, resourceType: rt,
                    isActive: act, ownerShortname: own,
                    ownerGroupShortname: og, entryShortname: entryShortname);
                rows.Add((sn, sp, subp, policies.ToArray()));
            }

            const int CHUNK_SIZE = 1000;
            var fixedCount = 0;
            for (var i = 0; i < rows.Count; i += CHUNK_SIZE)
            {
                var len = Math.Min(CHUNK_SIZE, rows.Count - i);
                fixedCount += await BulkUpdatePoliciesAsync(conn, tableName, rows, i, len);
            }
            Console.WriteLine($"  {tableName}: updated {fixedCount} row(s).");
            grandTotal += fixedCount;
        }

        if (!dryRun)
            Console.WriteLine($"Total rows fixed: {grandTotal}.");
        return;
    }

    case "create-users-folders":
    case "create_users_folders":
    {
        // Backfill: walk the users table and materialize each user's
        // personal/people/<shortname> tree (notifications, private, protected,
        // public, inbox) plus the parent folder under personal/people.
        //
        // Mirror of dmart/backend/data_adapters/sql/create_users_folders.py.
        // The runtime path normally covers this: ResourceFoldersCreationPlugin
        // fires on User-create and writes the same tree. But import (whether
        // dmart import or a Python json_to_db dump) goes through the entry
        // repository directly and bypasses the plugin manager — those users
        // arrive without their folders, and the only way to materialize them
        // after the fact is this command.
        //
        // Idempotent: each folder is checked first via EntryRepository.GetAsync
        // and skipped when present. owner_shortname is the user themselves,
        // matching the plugin and Python.
        var (_, dbInst) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues,
            "Error: Database not configured. Set DATABASE_* in config.env.");
        var entryRepo = new EntryRepository(dbInst);

        await using var conn = await dbInst.OpenAsync();
        await using var sel = new Npgsql.NpgsqlCommand(
            "SELECT shortname FROM users ORDER BY shortname", conn);
        var shortnames = new List<string>();
        await using (var r = await sel.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
                shortnames.Add(r.GetString(0));
        }
        Console.WriteLine($"Found {shortnames.Count} users");

        var created = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var sn in shortnames)
        {
            // Folder list mirrors ResourceFoldersCreationPlugin's User branch.
            // Kept inline so the backfill stays decoupled from plugin DI; if
            // the canonical list ever changes, both sites must update.
            var folders = new[]
            {
                (Space: "personal", Subpath: "/people",       Name: sn),
                (Space: "personal", Subpath: $"/people/{sn}", Name: "notifications"),
                (Space: "personal", Subpath: $"/people/{sn}", Name: "private"),
                (Space: "personal", Subpath: $"/people/{sn}", Name: "protected"),
                (Space: "personal", Subpath: $"/people/{sn}", Name: "public"),
                (Space: "personal", Subpath: $"/people/{sn}", Name: "inbox"),
            };
            foreach (var (spaceName, subpath, name) in folders)
            {
                try
                {
                    var existing = await entryRepo.GetAsync(spaceName, subpath, name, ResourceType.Folder);
                    if (existing is not null) { skipped++; continue; }

                    await entryRepo.UpsertAsync(new Entry
                    {
                        Uuid = Guid.NewGuid().ToString(),
                        Shortname = name,
                        SpaceName = spaceName,
                        Subpath = subpath,
                        ResourceType = ResourceType.Folder,
                        IsActive = true,
                        OwnerShortname = sn,
                        CreatedAt = Dmart.Utils.TimeUtils.Now(),
                        UpdatedAt = Dmart.Utils.TimeUtils.Now(),
                    });
                    created++;
                    Console.WriteLine($"Created folder ({spaceName}, {subpath}, {name}) for user {sn}");
                }
                catch (Exception ex)
                {
                    // Most common cause is a missing parent space ("personal"
                    // not yet bootstrapped). Log + keep going so one missing
                    // space doesn't take down the whole backfill.
                    failed++;
                    Console.Error.WriteLine($"Error creating folder ({spaceName}, {subpath}, {name}) for user {sn}: {ex.Message}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("===== DONE ======");
        Console.WriteLine($"Scanned {shortnames.Count} users, created {created} missing folders ({skipped} skipped, {failed} failed)");
        Environment.ExitCode = failed > 0 ? 2 : 0;
        return;
    }

    case "update_query_policies":
    case "update-query-policies":
    {
        // Recompute query_policies for every row in `entries` and write the
        // value back when it differs from what's stored. Mirrors Python's
        // update_query_policies.py — same name, same scope (Entries only),
        // same default batch size. Use this when a bulk in-place change
        // (e.g. an owner_shortname rename or an is_active toggle done via
        // direct SQL) leaves the materialized policies stale; the regular
        // /managed/request write path keeps them in sync going forward.
        //
        // Idempotent: rows whose recomputed policies match the stored array
        // are skipped without an UPDATE. Safe on a live DB; each UPDATE
        // touches only the query_policies column.
        var batchSize = 1000;
        for (var i = 0; i < serverArgs.Length; i++)
        {
            if (serverArgs[i] == "--batch-size" && i + 1 < serverArgs.Length
                && int.TryParse(serverArgs[i + 1], out var b) && b > 0)
                batchSize = b;
        }

        var (s, dbInst) = CliBootstrap.BuildOrExit(dotenvPath, dotenvValues,
            "Error: Database not configured. Set DATABASE_* in config.env.");

        Console.WriteLine($"Recomputing query_policies for entries in {s.DatabaseName}@{s.DatabaseHost}:{s.DatabasePort} (batch={batchSize})...");

        await using var conn = await dbInst.OpenAsync();

        var updated = 0;
        var offset = 0;
        while (true)
        {
            await using var sel = new Npgsql.NpgsqlCommand("""
                SELECT shortname, space_name, subpath, resource_type, is_active,
                       owner_shortname, owner_group_shortname, query_policies
                FROM entries
                ORDER BY space_name, subpath, shortname
                OFFSET $1 LIMIT $2
                """, conn);
            sel.Parameters.Add(new() { Value = offset });
            sel.Parameters.Add(new() { Value = batchSize });

            var rows = new List<(string sn, string sp, string subp, string rt, bool act,
                string own, string? og, string[] policies)>();
            await using (var r = await sel.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    rows.Add((
                        r.GetString(0), r.GetString(1), r.GetString(2),
                        r.GetString(3), r.GetBoolean(4),
                        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                        r.IsDBNull(7) ? Array.Empty<string>() : r.GetFieldValue<string[]>(7)));
                }
            }
            if (rows.Count == 0) break;
            Console.WriteLine($"Processing {rows.Count} entries...");

            // Recompute per page in C# (same QueryPolicies.Generate call as
            // before), drop rows whose stored value already matches (preserves
            // the idempotency contract), then ship the differing rows in one
            // chunked bulk UPDATE per CHUNK_SIZE rows. For a typical page
            // (batchSize=1000) that's one bulk UPDATE per page instead of up
            // to N per-row UPDATEs.
            var differing = new List<(string Shortname, string SpaceName, string Subpath, string[] Policies)>();
            foreach (var (sn, sp, subp, rt, act, own, og, current) in rows)
            {
                List<string> recomputed;
                try
                {
                    var entryShortname = rt == "folder" ? sn : null;
                    recomputed = Dmart.Utils.QueryPolicies.Generate(
                        spaceName: sp, subpath: subp, resourceType: rt,
                        isActive: act, ownerShortname: own ?? "dmart",
                        ownerGroupShortname: og, entryShortname: entryShortname);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error while computing query_policies for {sp}/{subp}/{sn}");
                    Console.WriteLine($"| {ex.Message}\n");
                    continue;
                }

                if (current.SequenceEqual(recomputed)) continue;
                differing.Add((sn, sp, subp, recomputed.ToArray()));
            }

            const int CHUNK_SIZE = 1000;
            for (var i = 0; i < differing.Count; i += CHUNK_SIZE)
            {
                var len = Math.Min(CHUNK_SIZE, differing.Count - i);
                updated += await BulkUpdatePoliciesAsync(conn, "entries", differing, i, len);
            }
            offset += rows.Count;
        }

        Console.WriteLine($"Updated query_policies for {updated} entries.");
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

    // .NET 10's OpenAPI generator emits `$ref: "#/components/schemas/<EnumName>"`
    // for every named enum it encounters, but the [JsonConverter] attribute on
    // our string enums (Status, ResourceType, …) blocks its built-in schema
    // introspection — leaving the $ref pointing at nothing and Swagger UI
    // reporting "Resolver error: Could not resolve reference". Inject the
    // string-enum schemas here from the converters' own WireValues. Overwrite
    // unconditionally: when an enum is reachable from an endpoint, the
    // generator pre-creates a null/empty schema under that name which we
    // need to replace, not preserve.
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSchema>();
        foreach (var (name, values) in Dmart.Models.Json.EnumMemberConverters.All)
        {
            var schema = new Microsoft.OpenApi.OpenApiSchema
            {
                Type = Microsoft.OpenApi.JsonSchemaType.String,
                Enum = new List<System.Text.Json.Nodes.JsonNode>(values.Count),
            };
            foreach (var v in values)
                schema.Enum.Add(System.Text.Json.Nodes.JsonValue.Create(v)!);
            document.Components.Schemas[name] = schema;
        }

        return Task.CompletedTask;
    });

    // Attach a sample payload to each request body schema we know about so
    // Swagger UI's "Try it out" form pre-fills with a working request. The
    // OpenApiExamples registry maps request body types to their samples;
    // adding a new entry there is enough — no per-endpoint wiring needed.
    //
    // Same place we patch JoinQuery.query: it's declared as `JsonElement?`
    // in C# to allow a free-form inner query body, but the generator emits
    // a $ref to a JsonElement schema it never defines. Replace that
    // property's schema with an inline "anything" so Swagger UI doesn't
    // print "Could not resolve reference: JsonElement".
    options.AddSchemaTransformer((schema, ctx, _) =>
    {
        if (Dmart.Api.OpenApiExamples.TryGet(ctx.JsonTypeInfo.Type, out var example))
            schema.Example = example;
        if (ctx.JsonTypeInfo.Type == typeof(Dmart.Models.Api.JoinQuery) && schema.Properties is not null)
            schema.Properties["query"] = new Microsoft.OpenApi.OpenApiSchema
            {
                // Type=Object communicates "expect a JSON object" to Swagger
                // UI (it'll render `{}` in Try-it-out) rather than the
                // description-only stub which leaves the UI guessing.
                Type = Microsoft.OpenApi.JsonSchemaType.Object,
                Description = "Inner Query body for this join (any JSON object).",
            };
        return Task.CompletedTask;
    });

    // Multipart endpoints can't use `.Accepts<TForm>("multipart/form-data")`
    // because their form marker classes hold IFormFile (an interface that
    // System.Text.Json source-gen can't emit metadata for, which the OpenAPI
    // resolver requires). And `.WithOpenApi(op => ...)` per-endpoint isn't
    // AOT-safe (IL2026/IL3050 — uses reflection). So inject the
    // multipart/form-data request body schemas here, keyed by path.
    options.AddDocumentTransformer((document, _, _) =>
    {
        Dmart.Api.OpenApiMultipartSchemas.Apply(document);
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

    // Publish the log directory to plugins via env. Each plugin is
    // responsible for opening its own `<shortname>.ljson.log` under this
    // path — the host does not intercept, format, or rotate plugin-emitted
    // lines. Subprocess plugins inherit the env from the dmart process;
    // in-process .so plugins read it with Environment.GetEnvironmentVariable.
    // The value is absolutized because subprocess plugins run with their own
    // working directory (the plugin's own folder), so a relative path would
    // resolve to the wrong place. Empty LogFile = no env exported, and the
    // plugin-side helper skips file logging on its own.
    //
    // dmart owns DMART_PLUGIN_LOG_DIR: a pre-existing value from the parent
    // env is overwritten so plugins always agree with the host on the log
    // directory. Operators who want a different location should set LogFile
    // in dmart's config rather than exporting this env var directly.
    if (!string.IsNullOrEmpty(logFile))
    {
        var dir = Path.GetDirectoryName(logFile);
        if (!string.IsNullOrEmpty(dir))
            Environment.SetEnvironmentVariable("DMART_PLUGIN_LOG_DIR", Path.GetFullPath(dir));
    }

    // Set minimum log level from config.env.
    if (Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var minLevel))
        builder.Logging.SetMinimumLevel(minLevel);

    // Suppress noisy ASP.NET framework logs.
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
    // Pin the dmart startup banner to Information so it survives even when
    // the global default is raised to Warning.
    builder.Logging.AddFilter("Dmart.Startup", LogLevel.Information);

    // Under systemd, when an on-disk LOG_FILE is configured, keep journald
    // quiet for per-request API logs. Operators set LOG_FILE precisely so
    // they can grep the .ljson.log; mirroring every "HTTP GET /foo → 200"
    // line to journald just blasts the system journal and risks rate-limit
    // dropping legitimately interesting lines. Apply the suppression at
    // the Console provider only — the FileLoggerProvider (registered
    // below) keeps receiving everything, so /var/lib/dmart/logs/*.ljson.log
    // remains the complete record. Startup banner ("Dmart.Startup",
    // "Microsoft.Hosting.Lifetime") and Warning+ from any source still
    // hit journald.
    if (isSystemd && !string.IsNullOrEmpty(logFile))
    {
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
            "Dmart.RequestLog", LogLevel.None);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
            "Microsoft.AspNetCore", LogLevel.Warning);
    }

    if (string.Equals(logFormat, "json", StringComparison.OrdinalIgnoreCase))
    {
        // Register the custom formatter directly in DI rather than via the
        // AOT-incompatible generic AddConsoleFormatter<TFormatter, TOptions>
        // overload (its TOptions binding goes through reflection).
        builder.Logging.Services.AddSingleton<
            Microsoft.Extensions.Logging.Console.ConsoleFormatter,
            Dmart.DmartJsonConsoleFormatter>();
        builder.Logging.AddConsole(o => o.FormatterName = Dmart.DmartJsonConsoleFormatter.FormatterName);
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

// Channel-auth registry — loads `~/.dmart/channels.json` (or
// settings.ChannelsConfigPath) once, pre-compiles regexes. The middleware
// short-circuits when EnableChannelAuth=false, so the singleton is essentially
// free in deployments that don't opt in.
builder.Services.AddSingleton<ChannelsRegistry>();

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
builder.Services.AddSingleton<HealthCheckRepository>();
builder.Services.AddSingleton<SpaceRepository>();

// Forwarded headers — configured here so the parameterless UseForwardedHeaders()
// picks up XForwardedFor + XForwardedProto for correct client IP and scheme.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                          | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;

    // Trust X-Forwarded-For ONLY from configured proxies, and only the
    // configured number of hops. This is what makes per-IP rate limiting work
    // behind nginx: ASP.NET walks the XFF chain right-to-left across exactly
    // ForwardLimit trusted hops and sets Connection.RemoteIpAddress to the real
    // client. A direct attacker (not coming via a trusted proxy) has their XFF
    // ignored, and one coming THROUGH the proxy can't extend the trusted-hop
    // count, so neither can spoof their rate-limit / log identity. Loopback
    // stays trusted by default (same-host nginx). See DmartSettings.TrustedProxies.
    var fwdSettings = new DmartSettings();
    builder.Configuration.GetSection("Dmart").Bind(fwdSettings);
    opts.ForwardLimit = Math.Max(1, fwdSettings.ForwardedForHopCount);
    var (trustedProxies, trustedNetworks) = fwdSettings.ParseTrustedProxies();
    foreach (var proxy in trustedProxies)
        opts.KnownProxies.Add(proxy);
    foreach (var network in trustedNetworks)
        opts.KnownIPNetworks.Add(network);
});

// Opt-in observability. No-op unless Dmart:OtlpEndpoint is set, so existing
// deployments and the test suite incur zero overhead by default.
{
    var telemetrySettings = new DmartSettings();
    builder.Configuration.GetSection("Dmart").Bind(telemetrySettings);
    builder.AddDmartTelemetry(telemetrySettings);
}

// Schema bootstrapper runs once on startup. AdminBootstrap MUST be registered AFTER
// SchemaInitializer — IHostedServices run StartAsync sequentially in registration order.
builder.Services.AddHostedService<SchemaInitializer>();
builder.Services.AddHostedService<AdminBootstrap>();

// IP-based rate limiter for authentication endpoints. Account lockout (on the
// user row) limits attempts per-account; this limits attempts per-IP so an
// attacker can't enumerate accounts by slowly cycling shortnames at the
// per-account threshold. Default 10 req/min/IP via AuthRateLimitPerMinute —
// legitimate users rarely hit it; operators can raise it for batch clients
// that repeatedly re-authenticate instead of caching the access token.
builder.Services.AddRateLimiter(opts =>
{
    opts.AddPolicy("auth-by-ip", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var permit = ctx.RequestServices
            .GetRequiredService<IOptions<DmartSettings>>().Value.AuthRateLimitPerMinute;
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
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
builder.Services.AddSingleton<UniquenessValidator>();
builder.Services.AddSingleton<EntryService>();
builder.Services.AddSingleton<QueryService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddSingleton<WorkflowService>();
// SpaceEventLogger captures inbound request headers (Python parity, minus
// cookie/authorization) into the audit log; needs IHttpContextAccessor.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SpaceEventLogger>();
builder.Services.AddSingleton<PluginManager>();
// Drains fire-and-forget concurrent plugin after-hooks on graceful shutdown.
builder.Services.AddHostedService<Dmart.Plugins.PluginDrainService>();
builder.Services.AddSingleton<LanguageLoader>();
builder.Services.AddSingleton<LockService>();
builder.Services.AddSingleton<ShortLinkService>();
builder.Services.AddSingleton<CsvService>();
builder.Services.AddSingleton<ImportExportService>();
builder.Services.AddSingleton<QrService>();
builder.Services.AddSingleton<WsConnectionManager>();

// Auth
builder.Services.AddSingleton<JwtIssuer>();
builder.Services.AddSingleton<SmsSender>();
builder.Services.AddSingleton<SmtpSender>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<SessionTokenHasher>(sp =>
    new SessionTokenHasher(sp.GetRequiredService<IOptions<DmartSettings>>().Value));
builder.Services.AddSingleton<OtpProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.GoogleProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.FacebookProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.AppleProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.OAuthUserResolver>();
builder.Services.AddSingleton<Dmart.Auth.OAuthCodeStore>();
builder.Services.AddSingleton<Dmart.Auth.OAuthClientStore>();
builder.Services.AddHostedService<Dmart.Auth.OAuthStoreSweeper>();
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

// Bridge DI into the [UnmanagedCallersOnly] static methods that back the
// DmartCallbacks struct handed to each native plugin's init() export.
// Stays alive for the process lifetime.
Dmart.Plugins.Native.NativePluginCallbacks.Services = app.Services;

// Register the shutdown hook for subprocess plugins so each one gets a
// clean stdin-close (EOF) when dmart starts shutting down. Paired with the
// SDK sample's SIGINT handling, this silences the KeyboardInterrupt trace
// that terminal Ctrl+C would otherwise elicit.
Dmart.Plugins.Native.NativePluginLoader.WireSubprocessShutdown(
    app.Services.GetRequiredService<IHostApplicationLifetime>());

// Drain the Npgsql connection pool on graceful shutdown so SIGTERM doesn't
// leave orphan backends attached to the dmart DB. Without this the next
// `dropdb` (CI rerun, local dev iteration) is blocked by stale sessions
// until they time out — the CI workflow currently `pg_terminate_backend`s
// them defensively pre-test, but the workaround masks a real gap in our
// shutdown handling. ClearAllPools closes idle connections immediately;
// in-flight ones get closed when their using-blocks dispose, which the
// ApplicationStopping → ApplicationStopped lifecycle already serialises
// behind.
{
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var poolLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dmart.Shutdown");
    lifetime.ApplicationStopped.Register(() =>
    {
        try
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            poolLog.LogInformation("npgsql pools cleared on shutdown");
        }
        catch (Exception ex)
        {
            // Best-effort — never let a shutdown-time pool error escape into
            // the host's exit code.
            poolLog.LogWarning(ex, "npgsql pool clear on shutdown threw (ignored)");
        }
    });
}

// Log the baked-in build version as the first line of the startup banner.
// Uses the Dmart.Startup category (pinned to Information by the AddFilter
// above) so it always survives, and is captured by the JSONL sink (LOG_FILE)
// as a structured {Version, Runtime} record.
{
    var v = typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "dev";
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Dmart.Startup")
        .LogInformation("dmart {Version} on .NET {Runtime}", v, Environment.Version);
}

// Wire structured logging into static QueryHelper so it doesn't use Console.Error.
Dmart.DataAdapters.Sql.QueryHelper.SetLogger(app.Services.GetRequiredService<ILoggerFactory>());

// Forwarded headers — must be first so RemoteIpAddress, Scheme and Host
// reflect the real client values behind any reverse proxy (Nginx, Traefik, ALB).
// Without this, rate-limiter IP partitioning uses the proxy IP and cookie
// Secure flags derive from the proxy→backend leg (usually plain HTTP).
app.UseForwardedHeaders();

// Exception handler.
// PG metadata (sqlstate, table, constraint, column, hint, MessageText)
// is kept in the server log for ops triage but never leaked to the wire —
// those fields expose schema internals and constraint names. The client
// response stays a minimal {code, message, type, info:[{cid}]} envelope
// matching the redacted style of the catch-all handler below.
static async Task WriteDbFailureAsync(HttpContext ctx, int httpStatus, int code, string message)
{
    if (ctx.Response.HasStarted) return;
    var cid = ctx.Response.Headers["X-Correlation-ID"].ToString();
    ctx.Response.StatusCode = httpStatus;
    ctx.Response.ContentType = "application/json";
    var body = Dmart.Models.Api.Response.Fail(code, message, ErrorTypes.Db,
        new List<Dictionary<string, object>> { new() { ["cid"] = cid } });
    await ctx.Response.WriteAsJsonAsync(body, DmartJsonContext.Default.Response);
}

// Same envelope shape as WriteDbFailureAsync but type=request — used when the
// underlying PG error is actually caused by malformed user input (e.g. an
// undefined column in a query.search expression) so clients branching on
// error.type don't classify a user mistake as a server-side DB fault.
static async Task WriteRequestFailureAsync(HttpContext ctx, int httpStatus, int code, string message)
{
    if (ctx.Response.HasStarted) return;
    var cid = ctx.Response.Headers["X-Correlation-ID"].ToString();
    ctx.Response.StatusCode = httpStatus;
    ctx.Response.ContentType = "application/json";
    var body = Dmart.Models.Api.Response.Fail(code, message, ErrorTypes.Request,
        new List<Dictionary<string, object>> { new() { ["cid"] = cid } });
    await ctx.Response.WriteAsJsonAsync(body, DmartJsonContext.Default.Response);
}

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Npgsql.PostgresException ex)
    {
        var cid = ctx.Response.Headers["X-Correlation-ID"].ToString();
        var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ExceptionHandler");
        logger?.LogError(ex,
            "Postgres error cid={Cid} sqlstate={SqlState} table={Table} constraint={Constraint}",
            cid, ex.SqlState, ex.TableName, ex.ConstraintName);
        if (ex.SqlState == "23505")
        {
            // UNIQUE-violation. Use SHORTNAME_ALREADY_EXIST so the internal
            // code matches the service-layer catches in Services/EntryService.cs
            // and Dmart.SqlAdapter — clients branch on the same integer
            // regardless of which layer caught the exception. HTTP 409 is
            // the conventional status for a conflict.
            //
            // PG's Detail names the offending (column, value) pair — surface
            // ONLY the column to callers so they know which field collided
            // instead of guessing. The value is deliberately redacted: most
            // unique columns on `users` are identifiers (email, msisdn,
            // google_id, facebook_id, apple_id, shortname) and echoing the
            // value back on a duplicate-key failure on /user/create would
            // let an attacker enumerate registered identifiers by probing.
            // The value is still in the server log under cid for ops triage.
            //
            // Detail can be suppressed (terse `log_error_verbosity`, some
            // managed-PG vendors strip it), so we fall back to deriving the
            // column from the constraint name when Detail is unavailable.
            var (detailKey, _) = Dmart.Utils.PgErrorParsing.ExtractUniqueViolation(ex.Detail);
            var column = detailKey
                         ?? Dmart.Utils.PgErrorParsing.ExtractUniqueViolationKey(ex.ConstraintName, ex.TableName);
            var message = column is not null
                ? $"resource with this {column} already exists"
                : "resource already exists";
            await WriteDbFailureAsync(ctx, StatusCodes.Status409Conflict,
                Dmart.Models.Api.InternalErrorCode.SHORTNAME_ALREADY_EXIST,
                message);
        }
        else if (ex.SqlState == "42703")
        {
            // undefined_column. Reached when a query.search expression
            // references a field that isn't a real column — e.g. `@asd:123`
            // when `asd` does not exist on entries. SearchExpressionParser
            // only validates syntax (SafeColumnIdent), so unknown names
            // pass through and PG rejects the generated SQL. Surface a
            // request-level error pointing users at the @payload.body.<field>
            // form instead of leaving them with the opaque 430.
            var col = Dmart.Utils.PgErrorParsing.ExtractUndefinedColumn(ex.MessageText);
            var message = col is null
                ? "Unknown search field. To search a custom payload field, use '@payload.body.<field>:<value>'."
                : $"Unknown search field '{col}'. To search a custom payload field, use '@payload.body.{col}:<value>' instead of '@{col}:<value>'.";
            await WriteRequestFailureAsync(ctx, StatusCodes.Status400BadRequest,
                Dmart.Models.Api.InternalErrorCode.INVALID_DATA, message);
        }
        else
        {
            await WriteDbFailureAsync(ctx, StatusCodes.Status500InternalServerError,
                Dmart.Models.Api.InternalErrorCode.SOMETHING_WRONG,
                $"Database error. Reference: {cid}");
        }
    }
    catch (Npgsql.NpgsqlException ex)
    {
        // Transport/connection-level Npgsql failure (pool exhaustion, socket
        // reset, auth handshake). Surfaced with type="db" so existing client
        // branching still works, but no Npgsql message is included.
        var cid = ctx.Response.Headers["X-Correlation-ID"].ToString();
        var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ExceptionHandler");
        logger?.LogError(ex, "Npgsql error cid={Cid}", cid);
        await WriteDbFailureAsync(ctx, StatusCodes.Status500InternalServerError,
            Dmart.Models.Api.InternalErrorCode.SOMETHING_WRONG,
            $"Database error. Reference: {cid}");
    }
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

// Strip empty object properties ("", [], {}) from JSON responses globally.
// Registered AFTER UseResponseCompression so the strip happens on uncompressed
// JSON; the compressed bytes that go on the wire reflect the trimmed body.
// Intentional divergence from Python dmart — see middleware comment.
app.UseJsonStripEmpties();

// Correlation ID
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Correlation-ID"] = ctx.Request.Headers.TryGetValue("X-Correlation-ID", out var corr)
        ? corr.ToString()
        : Guid.NewGuid().ToString("N");
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

// Channel-auth gate (Python parity: utils/middleware.py::ChannelMiddleware).
// No-op when ENABLE_CHANNEL_AUTH=false. Placed after the response-headers
// middleware so OPTIONS preflights and CORS headers are handled first, and
// before route matching so the gate is evaluated for every request, including
// 404 paths that would otherwise probe the surface area unauthenticated.
app.UseChannelAuth();

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
        // Wrap Response.Body in a byte counter so we can distinguish
        // "handler wrote something" from "handler wrote nothing" regardless
        // of TestServer / Kestrel buffering semantics. HasStarted alone is
        // unreliable: TestServer may buffer writes without flipping the
        // flag, and ContentLength may not be set on small WriteAsync calls.
        var originalBody = ctx.Response.Body;
        var counter = new BodyByteCounterStream(originalBody);
        ctx.Response.Body = counter;
        try
        {
            await next();
        }
        finally
        {
            ctx.Response.Body = originalBody;
        }

        if (ctx.Response.HasStarted) return;
        if (counter.BytesWritten > 0) return;
        if (ctx.Request.Path.StartsWithSegments(cxbPath)) return;
        if (ctx.Request.Path.StartsWithSegments(catPath)) return;

        // Wrap empty-body request-side errors in the canonical envelope so
        // every dmart response shape is uniform. Auth (401/403), rate-limit
        // (429), and 5xx are intentionally NOT wrapped here: JwtBearer,
        // AuthorizationMiddleware, and the exception handler each emit
        // their own typed bodies via paths we don't want to clobber.
        var status = ctx.Response.StatusCode;
        if (status is not (400 or 404 or 405 or 415)) return;

        var method = ctx.Request.Method;
        var path = ctx.Request.Path.ToString();
        int code;
        string message;
        // Preserve the wire status code so the HTTP semantic the handler
        // (or routing) chose survives. The one exception is the existing
        // "unmapped route" case, which the Python parity convention maps
        // to 422 — keep that mapping so the established INVALID_ROUTE
        // contract doesn't shift.
        int wireStatus = status;

        switch (status)
        {
            // GetEndpoint() is non-null iff a route matched and its handler
            // ran. Null endpoint = unmapped URL.
            case 404 when ctx.GetEndpoint() is null:
                code = Dmart.Models.Api.InternalErrorCode.INVALID_ROUTE;
                message = $"Route not found: {method} {path}";
                wireStatus = 422;
                break;
            case 404:
                code = Dmart.Models.Api.InternalErrorCode.OBJECT_NOT_FOUND;
                message = $"Resource not found: {method} {path}";
                break;
            // 405 from ASP.NET's routing: URL pattern matched but the HTTP
            // method did not. Routing populates `Allow: <methods>` on the
            // empty response; surface the list in the message.
            case 405:
                var allowed = ctx.Response.Headers.Allow.ToString();
                code = Dmart.Models.Api.InternalErrorCode.INVALID_ROUTE;
                message = string.IsNullOrEmpty(allowed)
                    ? $"Method not allowed: {method} {path}"
                    : $"Method not allowed: {method} {path}; allowed: {allowed}";
                break;
            case 415:
                code = Dmart.Models.Api.InternalErrorCode.INVALID_DATA;
                message = $"Unsupported media type: {method} {path}";
                break;
            case 400:
                code = Dmart.Models.Api.InternalErrorCode.INVALID_DATA;
                message = $"Bad request: {method} {path}";
                break;
            default:
                return;
        }

        var body = Dmart.Models.Api.Response.Fail(code, message, ErrorTypes.Request);
        // Clear any Content-Length the framework set on its empty error
        // response, otherwise Kestrel refuses to overwrite with the body
        // length we're about to write.
        ctx.Response.ContentLength = null;
        ctx.Response.StatusCode = wireStatus;
        await ctx.Response.WriteAsJsonAsync(body, Dmart.Models.Json.DmartJsonContext.Default.Response);
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
    <meta http-equiv="Content-Security-Policy" content="default-src 'self'; style-src 'self' 'unsafe-inline' https://unpkg.com; script-src 'self' 'unsafe-inline' https://unpkg.com; img-src 'self' data:;" />
    <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui.css" />
</head>
<body>
    <div id="swagger-ui"></div>
    <script src="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui-bundle.js"></script>
    <script>
        SwaggerUIBundle({ url: 'openapi.json', dom_id: '#swagger-ui' });
    </script>
</body>
</html>
""", "text/html")).ExcludeFromDescription();
app.MapGet("/", () => Results.Content("{\"status\":\"success\",\"message\":\"DMART-CS API\"}", "application/json")).WithTags("Root");
app.MapHealth();

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

// Load embedded translation files once at startup (LanguageLoader is the
// single source of truth for OTP/SMS message bodies).
app.Services.GetRequiredService<LanguageLoader>().Load();

// Load plugins + mount API plugin routes
{
    // Register plugin type → shortname mappings so LogSink and the console
    // formatter can prepend `[<shortname>]` to log lines emitted via
    // ILogger<TPlugin>. Done before LoadAsync so any startup log lines from
    // a plugin's constructor or hook registration are already tagged.
    foreach (var p in app.Services.GetServices<IHookPlugin>())
        LogSink.RegisterBuiltinPlugin(p.GetType(), p.Shortname);
    foreach (var p in app.Services.GetServices<IApiPlugin>())
        LogSink.RegisterBuiltinPlugin(p.GetType(), p.Shortname);

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
}
