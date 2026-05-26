using Dmart.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Dmart.Cli;

// `dmart preflight <path>` — filesystem integrity scanner + auto-fixer
// for large dmart migrations. Walks the source tree, detects duplicate
// UUIDs / broken owner_shortname references / schema-noncompliant
// payloads, and (by default) rewrites the source in place to make it
// import-clean.
//
// Mirrors the operator-developed shell scripts under admin_scripts/
// (import-preflight.sh, regen-bad-owners.sh, regen-duplicate-uuids.sh)
// in one in-process, AOT-clean subcommand.
//
// Exit codes:
//   0 — clean source, or all detected issues successfully auto-fixed
//   1 — issues remain after the run (e.g. dry-run mode found work to
//       do, or schema violations need operator attention)
//   2 — tool error (bad args, unreadable source path)
public static class PreflightCommand
{
    public static async Task<int> Run(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return 0;  // --help printed

        // Stand up a minimal logger that goes straight to the console.
        // Preflight is a long-running standalone subcommand that doesn't
        // share the server's IConfiguration/IOptions pipeline, so we
        // build a small dedicated factory instead of going through
        // CliBootstrap (no DB needed either — this is pure filesystem).
        using var loggerFactory = LoggerFactory.Create(b => b
            .AddConsole(o => o.FormatterName = ConsoleFormatterNames.Simple)
            .SetMinimumLevel(opts.Verbose ? LogLevel.Debug : LogLevel.Information));
        var log = loggerFactory.CreateLogger<PreflightService>();
        var svc = new PreflightService(log);

        PreflightReport report;
        try
        {
            report = await svc.RunAsync(opts);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"preflight: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"preflight: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        PrintSummary(report);

        // Exit policy:
        //   * dry-run: 1 iff any issues found (operator should re-run
        //     without --dry-run to apply fixes)
        //   * default: 1 iff any unfixable issues remain (schema
        //     violations, parse errors) AFTER the auto-fix pass.
        if (report.DryRun)
            return report.Issues.Count == 0 ? 0 : 1;

        var unfixed = report.Issues.Count(i => i.Severity is "skip" or "error");
        return unfixed == 0 ? 0 : 1;
    }

    private static PreflightOptions? ParseArgs(string[] args)
    {
        string? path = null;
        bool dryRun = false;
        int workers = Environment.ProcessorCount;
        string? outputDir = null;
        int sample = 20;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    PrintHelp();
                    return null;
                case "--dry-run":
                    dryRun = true; break;
                case "--workers" when i + 1 < args.Length:
                    workers = int.TryParse(args[++i], out var w) ? w : workers; break;
                case "--output-dir" when i + 1 < args.Length:
                    outputDir = args[++i]; break;
                case "--sample" when i + 1 < args.Length:
                    sample = int.TryParse(args[++i], out var s) ? s : sample; break;
                case "-v":
                case "--verbose":
                    verbose = true; break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"preflight: unknown argument '{args[i]}'");
                        PrintHelp();
                        return null;
                    }
                    if (path is not null)
                    {
                        Console.Error.WriteLine("preflight: only one source path is accepted");
                        return null;
                    }
                    path = args[i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            PrintHelp();
            return null;
        }

        return new PreflightOptions(path, dryRun, workers, outputDir, sample, verbose);
    }

    private static void PrintSummary(PreflightReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"preflight summary ({(report.DryRun ? "DRY-RUN" : "APPLIED")})");
        Console.WriteLine($"  source           : {report.SourcePath}");
        Console.WriteLine($"  output           : {report.OutputDir}");
        Console.WriteLine($"  meta files       : {report.TotalMetaFiles:N0}");
        Console.WriteLine($"  total issues     : {report.Issues.Count:N0}");
        Console.WriteLine();
        foreach (var group in report.Issues.GroupBy(i => i.Kind).OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {group.Key,-22} {group.Count(),8:N0}");
        }
        Console.WriteLine();
        Console.WriteLine($"  reports: {Path.Combine(report.OutputDir, "summary.json")}");
        if (report.Issues.Any(i => i.Kind == "schema-violation"))
            Console.WriteLine($"           {Path.Combine(report.OutputDir, "schema-violations.jsonl")}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            dmart preflight — scan a legacy dmart filesystem export for integrity issues.

            Usage: dmart preflight [options] <spaces-folder>

            Detects + (by default) auto-fixes three classes of issue before `dmart import`:
              * duplicate UUIDs    — keeps first sorted path, regenerates others
              * missing owners     — swaps owner_shortname to "dmart" sentinel
              * schema violations  — flags + writes to schema-violations.jsonl
                                     (not auto-fixed; operator decides)

            A summary.json and (if applicable) schema-violations.jsonl land in the
            --output-dir directory (default: ./preflight-<timestamp>).

            Options:
              --dry-run            Report only, don't rewrite source files (default: apply fixes)
              --workers <N>        Parallel JSON parsers (default: nproc)
              --output-dir <dir>   Where to write reports (default: ./preflight-<timestamp>)
              --sample <N>         Per-issue sample size in summary (default: 20; full lists
                                   are always in schema-violations.jsonl)
              -v, --verbose        More logging
              -h, --help           This help

            Exit codes:
              0 — clean, or all issues auto-fixed (default mode)
              1 — issues remain after the run (dry-run with findings, or schema violations
                  that need operator attention)
              2 — tool error (bad args, unreadable source)

            Examples:
              # Report only:
              dmart preflight /var/lib/dmart/spaces --dry-run

              # Apply auto-fixes (default), keep the report:
              dmart preflight /var/lib/dmart/spaces

              # 16-worker scan with custom output:
              dmart preflight /var/lib/dmart/spaces --workers 16 --output-dir /tmp/pf
            """);
    }
}
