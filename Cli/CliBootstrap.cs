using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Dmart.Cli;

// Shared bootstrap for CLI subcommands that need a configured Db (set_password,
// check, export, import, migrate, fix_query_policies). Each of those used to
// repeat the same 8-line block:
//   - build IConfiguration from dotenv values + env vars
//   - bind into DmartSettings
//   - construct a Db
//   - refuse to proceed when Db isn't configured (exit 1)
//
// BuildOrExit consolidates that into one call. The error message is
// per-caller because different subcommands historically surfaced slightly
// different wording (some "Database not configured", others point at the
// specific DATABASE_* keys). Preserved verbatim to avoid behavior drift for
// anyone grepping output in a script.
//
// On the "not configured" path the helper calls Environment.Exit(1) rather
// than throwing, mirroring the pre-existing `Environment.ExitCode = 1; return;`
// semantics of every caller — the process terminates immediately with the
// same exit code, and the caller never sees the tuple.
internal static class CliBootstrap
{
    public static (DmartSettings Settings, Db Db) BuildOrExit(
        string? dotenvPath,
        IDictionary<string, string?> dotenvValues,
        string? dbRequiredErrorMessage = null)
    {
        var cfgBuilder = new ConfigurationBuilder();
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        var db = new Db(Options.Create(s));
        if (!db.IsConfigured)
        {
            Console.Error.WriteLine(dbRequiredErrorMessage ?? "Database not configured");
            Environment.Exit(1);
        }
        return (s, db);
    }
}
