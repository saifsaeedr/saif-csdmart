using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.Cli;
using Dmart.Models.Json;
using Json.Schema;

namespace Dmart.Services;

// Filesystem integrity scanner + auto-fixer for `dmart preflight`.
// Pure file I/O — no DB connection, no live dmart server needed. Designed
// to walk a 24M-entry / 400GB legacy export and either report or repair
// the issue classes that would otherwise blow up `dmart import`.
//
// Three scanners run in this order:
//   1. UuidScanner       — detects meta files sharing a UUID. Repair:
//                          keep first (sorted-path order), regen rest.
//                          Mirrors admin_scripts/regen-duplicate-uuids.sh
//                          but in-process and AOT-clean.
//   2. OwnerScanner      — detects owner_shortname values that don't
//                          exist in the source's user dump. Repair: swap
//                          to "dmart" sentinel (matches the EnsureOwner
//                          fallback ImportExportService already applies
//                          at import time — but doing it here lets the
//                          operator audit which entries were rewritten).
//   3. SchemaScanner     — for entries that reference a schema, validates
//                          their payload body against the schema sitting
//                          IN the source filesystem. Violations are
//                          flagged + skipped (not repaired); a sidecar
//                          schema-violations.jsonl lists every skipped
//                          path so a downstream import wrapper can
//                          consume it.
//
// Auto-fix is the default; `--dry-run` short-circuits the apply phase
// and just writes the report. The three scanner phases always run
// sequentially because the OwnerScanner needs the user-shortname
// universe assembled, and the SchemaScanner needs the schemas located.
// Within a phase, files are processed in parallel with
// Environment.ProcessorCount workers.
public sealed class PreflightService
{
    private readonly ILogger<PreflightService> _log;

    public PreflightService(ILogger<PreflightService> log)
    {
        _log = log;
    }

    // ===================================================================
    // Public entry point
    // ===================================================================

    public async Task<PreflightReport> RunAsync(PreflightOptions opts, CancellationToken ct = default)
    {
        if (!Directory.Exists(opts.Path))
            throw new DirectoryNotFoundException($"preflight: source path '{opts.Path}' does not exist");

        var workers = Math.Clamp(opts.Workers, 1, 64);
        var outDir = ResolveOutputDir(opts);
        Directory.CreateDirectory(outDir);

        _log.LogInformation("preflight: scanning {Source} ({Workers} workers, output={OutDir}, dry-run={DryRun})",
            opts.Path, workers, outDir, opts.DryRun);

        // Enumerate all meta.*.json files once. The walk itself is the
        // single most expensive operation at 24M-entry scale, so we pay
        // it once and feed the resulting list to every scanner.
        var metaFiles = await EnumerateMetaFilesAsync(opts.Path, ct);
        _log.LogInformation("preflight: discovered {Count} meta files", metaFiles.Count);

        var allIssues = new List<PreflightIssue>();

        // -- Phase 1: UUID dedup ----------------------------------------
        var uuidScanner = new UuidScanner(_log);
        var uuidIssues = await uuidScanner.ScanAsync(metaFiles, workers, ct);
        if (!opts.DryRun)
            await UuidScanner.ApplyRepairsAsync(uuidIssues, ct);
        allIssues.AddRange(uuidIssues);

        // -- Phase 2: owner_shortname FK ---------------------------------
        var ownerScanner = new OwnerScanner(_log);
        var ownerIssues = await ownerScanner.ScanAsync(metaFiles, workers, ct);
        if (!opts.DryRun)
            await OwnerScanner.ApplyRepairsAsync(ownerIssues, ct);
        allIssues.AddRange(ownerIssues);

        // -- Phase 3: schema validation ----------------------------------
        var schemaScanner = new SchemaScanner(_log, opts.Path);
        var schemaIssues = await schemaScanner.ScanAsync(metaFiles, workers, ct);
        // Schema violations are NEVER auto-fixed — they're written to a
        // sidecar so a downstream import wrapper can filter the paths.
        // Operator can re-run preflight after editing the bad payloads.
        await WriteSchemaSidecarAsync(outDir, schemaIssues, ct);
        allIssues.AddRange(schemaIssues);

        // -- Summary -----------------------------------------------------
        var report = new PreflightReport(
            SourcePath: opts.Path,
            OutputDir: outDir,
            DryRun: opts.DryRun,
            TotalMetaFiles: metaFiles.Count,
            Issues: allIssues);

        await WriteSummaryAsync(outDir, report, opts.Sample, ct);
        return report;
    }

    // ===================================================================
    // Internal helpers
    // ===================================================================

    private static string ResolveOutputDir(PreflightOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.OutputDir)) return opts.OutputDir;
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(Environment.CurrentDirectory, $"preflight-{ts}");
    }

    // Walk the source tree and return every file that matches the
    // meta.*.json shape used by dmart's export layout. We accept ANY
    // file named meta.<rt>.json under a .dm/ directory plus the
    // top-level meta.space.json files at .dm/meta.space.json.
    private static async Task<IReadOnlyList<string>> EnumerateMetaFilesAsync(
        string root, CancellationToken ct)
    {
        // EnumerateFiles with AllDirectories is the cheapest disk walk
        // .NET offers; we filter the filename pattern inline rather than
        // adding a secondary find step.
        return await Task.Run(() =>
        {
            var list = new List<string>(capacity: 1 << 14);
            foreach (var file in Directory.EnumerateFiles(root, "meta.*.json", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                list.Add(file);
            }
            return (IReadOnlyList<string>)list;
        }, ct);
    }

    private static async Task WriteSchemaSidecarAsync(
        string outDir, IReadOnlyList<PreflightIssue> schemaIssues, CancellationToken ct)
    {
        var path = Path.Combine(outDir, "schema-violations.jsonl");
        await using var w = new StreamWriter(path);
        foreach (var issue in schemaIssues.Where(i => i.Kind == "schema-violation"))
        {
            // One JSON object per line — matches dmart's existing
            // import-failures.jsonl convention so downstream tooling
            // can ingest both with the same parser.
            var obj = new Dictionary<string, object?>
            {
                ["path"] = issue.Path,
                ["kind"] = issue.Kind,
                ["severity"] = issue.Severity,
                ["action"] = issue.Action,
                ["details"] = issue.Details,
            };
            await w.WriteLineAsync(JsonSerializer.Serialize(obj, DmartJsonContext.Default.DictionaryStringObject));
            ct.ThrowIfCancellationRequested();
        }
    }

    private static async Task WriteSummaryAsync(
        string outDir, PreflightReport report, int sample, CancellationToken ct)
    {
        var path = Path.Combine(outDir, "summary.json");
        var byKind = report.Issues.GroupBy(i => i.Kind).ToDictionary(
            g => g.Key,
            g => new Dictionary<string, object?>
            {
                ["count"] = g.Count(),
                ["sample"] = g.Take(sample).Select(i => i.Path).ToList(),
            } as object);

        var summary = new Dictionary<string, object?>
        {
            ["source"] = report.SourcePath,
            ["dry_run"] = report.DryRun,
            ["meta_files_scanned"] = report.TotalMetaFiles,
            ["total_issues"] = report.Issues.Count,
            ["by_kind"] = byKind,
            ["generated_at"] = DateTimeOffset.UtcNow.ToString("o"),
        };

        var json = JsonSerializer.Serialize(summary, DmartJsonContext.Default.DictionaryStringObject);
        await File.WriteAllTextAsync(path, json, ct);
    }

    // ===================================================================
    // Scanner 1 — duplicate UUIDs
    // ===================================================================

    private sealed class UuidScanner
    {
        private readonly ILogger _log;
        public UuidScanner(ILogger log) { _log = log; }

        public async Task<List<PreflightIssue>> ScanAsync(
            IReadOnlyList<string> metaFiles, int workers, CancellationToken ct)
        {
            // ConcurrentDictionary<uuid, paths>. The Inner Bag is a
            // ConcurrentBag to keep the lock-free property end-to-end —
            // List<T>.Add isn't thread-safe and we'd otherwise serialize
            // every Append on a per-key lock.
            var byUuid = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.Ordinal);

            await Parallel.ForEachAsync(metaFiles,
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                async (file, c) =>
                {
                    string? uuid = await ReadUuidAsync(file, c);
                    if (string.IsNullOrEmpty(uuid)) return;
                    var bag = byUuid.GetOrAdd(uuid, _ => new ConcurrentBag<string>());
                    bag.Add(file);
                });

            var issues = new List<PreflightIssue>();
            foreach (var (uuid, bag) in byUuid)
            {
                if (bag.Count < 2) continue;
                var paths = bag.OrderBy(p => p, StringComparer.Ordinal).ToList();
                // Keep paths[0], regen rest.
                for (int i = 1; i < paths.Count; i++)
                {
                    issues.Add(new PreflightIssue
                    {
                        Path = paths[i],
                        Kind = "duplicate-uuid",
                        Severity = "fixable",
                        Action = "would-regen-uuid",
                        Details = new Dictionary<string, object>
                        {
                            ["old_uuid"] = uuid,
                            ["kept"] = paths[0],
                            ["group_size"] = paths.Count,
                        }
                    });
                }
            }
            _log.LogInformation("preflight uuid-scan: {DupGroups} duplicate groups, {Affected} files to regen",
                issues.Count == 0 ? 0 : issues.GroupBy(i => i.Details!["old_uuid"]).Count(),
                issues.Count);
            return issues;
        }

        public static async Task ApplyRepairsAsync(IReadOnlyList<PreflightIssue> issues, CancellationToken ct)
        {
            foreach (var issue in issues)
            {
                ct.ThrowIfCancellationRequested();
                if (issue.Kind != "duplicate-uuid") continue;
                var newUuid = Guid.NewGuid().ToString();
                if (await RewriteJsonFieldAsync(issue.Path, "uuid", newUuid, ct))
                {
                    issue.Details!["new_uuid"] = newUuid;
                    SetAction(issue, "regenerated");
                }
                else
                {
                    SetAction(issue, "regen-failed");
                }
            }
        }

        private static async Task<string?> ReadUuidAsync(string path, CancellationToken ct)
        {
            try
            {
                await using var s = File.OpenRead(path);
                using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                return doc.RootElement.TryGetProperty("uuid", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString() : null;
            }
            catch
            {
                // Parse errors surface separately if the operator wants them;
                // for UUID dedup we just skip unreadable files.
                return null;
            }
        }
    }

    // ===================================================================
    // Scanner 2 — owner_shortname FK
    // ===================================================================

    private sealed class OwnerScanner
    {
        private const string Sentinel = "dmart";
        private readonly ILogger _log;
        public OwnerScanner(ILogger log) { _log = log; }

        public async Task<List<PreflightIssue>> ScanAsync(
            IReadOnlyList<string> metaFiles, int workers, CancellationToken ct)
        {
            // Phase 2a: assemble the user shortname universe. Any meta
            // file named meta.user.json contributes its shortname.
            // Always include the bootstrap admin sentinel.
            var users = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            users.TryAdd(Sentinel, 0);
            await Parallel.ForEachAsync(
                metaFiles.Where(p => p.EndsWith("/meta.user.json", StringComparison.Ordinal)),
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                async (file, c) =>
                {
                    var sn = await ReadFieldAsync(file, "shortname", c);
                    if (!string.IsNullOrEmpty(sn)) users.TryAdd(sn, 0);
                });

            // Phase 2b: walk all OTHER meta files, flag any owner_shortname
            // that isn't in the universe.
            var issues = new ConcurrentBag<PreflightIssue>();
            await Parallel.ForEachAsync(metaFiles,
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                async (file, c) =>
                {
                    var owner = await ReadFieldAsync(file, "owner_shortname", c);
                    // Empty owner is handled by ImportExportService.EnsureOwner
                    // (silent fallback to sentinel). For the report we still
                    // surface it so the operator sees what's about to be
                    // rewritten.
                    if (owner is null) return;
                    if (string.IsNullOrEmpty(owner) || !users.ContainsKey(owner))
                    {
                        issues.Add(new PreflightIssue
                        {
                            Path = file,
                            Kind = "missing-owner",
                            Severity = "fixable",
                            Action = "would-swap-owner",
                            Details = new Dictionary<string, object>
                            {
                                ["old_owner"] = owner,
                                ["new_owner"] = Sentinel,
                            }
                        });
                    }
                });

            var list = issues.ToList();
            _log.LogInformation("preflight owner-scan: {Count} files reference missing owners", list.Count);
            return list;
        }

        public static async Task ApplyRepairsAsync(IReadOnlyList<PreflightIssue> issues, CancellationToken ct)
        {
            foreach (var issue in issues)
            {
                ct.ThrowIfCancellationRequested();
                if (issue.Kind != "missing-owner") continue;
                if (await RewriteJsonFieldAsync(issue.Path, "owner_shortname", Sentinel, ct))
                    SetAction(issue, "swapped");
                else
                    SetAction(issue, "swap-failed");
            }
        }
    }

    // ===================================================================
    // Scanner 3 — schema validation
    // ===================================================================

    private sealed class SchemaScanner
    {
        private readonly ILogger _log;
        private readonly string _sourceRoot;
        private readonly ConcurrentDictionary<(string Space, string Shortname), JsonSchema?> _schemaCache = new();
        private static readonly EvaluationOptions EvalOptions = new() { OutputFormat = OutputFormat.List };

        public SchemaScanner(ILogger log, string sourceRoot)
        {
            _log = log;
            _sourceRoot = sourceRoot;
        }

        public async Task<List<PreflightIssue>> ScanAsync(
            IReadOnlyList<string> metaFiles, int workers, CancellationToken ct)
        {
            var issues = new ConcurrentBag<PreflightIssue>();
            await Parallel.ForEachAsync(metaFiles,
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                async (file, c) => await ScanOneAsync(file, issues, c));
            var list = issues.ToList();
            _log.LogInformation("preflight schema-scan: {Count} payloads fail their schema", list.Count);
            return list;
        }

        private async Task ScanOneAsync(string file, ConcurrentBag<PreflightIssue> issues, CancellationToken ct)
        {
            JsonNode? root;
            try
            {
                await using var s = File.OpenRead(file);
                root = await JsonNode.ParseAsync(s, cancellationToken: ct);
            }
            catch
            {
                issues.Add(new PreflightIssue
                {
                    Path = file,
                    Kind = "parse-error",
                    Severity = "error",
                    Action = "skipped",
                });
                return;
            }

            if (root is not JsonObject obj) return;
            var schemaShortname = obj["payload"]?["schema_shortname"]?.GetValue<string?>();
            var body = obj["payload"]?["body"];
            if (string.IsNullOrEmpty(schemaShortname) || body is null) return;

            var space = ExtractSpace(file);
            if (space is null) return;

            var schema = await GetCompiledAsync(space, schemaShortname, ct);
            if (schema is null) return;  // schema absent — lenient, same as runtime

            var bodyEl = JsonDocument.Parse(body.ToJsonString()).RootElement;
            var result = schema.Evaluate(bodyEl, EvalOptions);
            if (result.IsValid) return;

            var errors = new List<string>();
            Collect(result, errors);
            issues.Add(new PreflightIssue
            {
                Path = file,
                Kind = "schema-violation",
                Severity = "skip",
                Action = "will-skip-on-import",
                Details = new Dictionary<string, object>
                {
                    ["schema_shortname"] = schemaShortname,
                    ["space"] = space,
                    ["errors"] = errors,
                }
            });
        }

        private async Task<JsonSchema?> GetCompiledAsync(string space, string shortname, CancellationToken ct)
        {
            var key = (space, shortname);
            if (_schemaCache.TryGetValue(key, out var cached)) return cached;

            // Look for meta.schema.json in canonical subpaths inside the
            // space root. The actual JSON Schema document lives in
            // payload.body of the schema entry — same shape the runtime
            // expects (see Services/SchemaValidator.cs:60-83).
            foreach (var sub in new[] { ".dm/schema", ".dm/schemas", "schema/.dm", "schemas/.dm" })
            {
                var candidate = Path.Combine(_sourceRoot, space, sub, shortname, "meta.schema.json");
                if (!File.Exists(candidate)) continue;
                try
                {
                    var json = await File.ReadAllTextAsync(candidate, ct);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("payload", out var payload)) continue;
                    if (!payload.TryGetProperty("body", out var bodyEl)) continue;
                    var schemaJson = bodyEl.GetRawText();
                    var compiled = JsonSchema.FromText(schemaJson);
                    _schemaCache[key] = compiled;
                    return compiled;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "preflight: failed to compile schema {Space}/{Shortname}", space, shortname);
                    return null;
                }
            }
            _schemaCache[key] = null;
            return null;
        }

        private string? ExtractSpace(string metaPath)
        {
            // metaPath is rooted at _sourceRoot; the first directory
            // component is the space name.
            var rel = Path.GetRelativePath(_sourceRoot, metaPath);
            var sep = rel.IndexOf(Path.DirectorySeparatorChar);
            return sep > 0 ? rel[..sep] : null;
        }

        private static void Collect(EvaluationResults r, List<string> errors)
        {
            if (r.Errors is not null)
                foreach (var (key, msg) in r.Errors)
                    errors.Add($"{r.InstanceLocation}: {key}: {msg}");
            if (r.Details is { Count: > 0 })
                foreach (var d in r.Details) Collect(d, errors);
        }
    }

    // ===================================================================
    // Shared helpers
    // ===================================================================

    // Atomic JSON-field rewrite. Reads the file into a JsonNode, sets the
    // field, writes to <path>.tmp, then renames. Matches the pattern from
    // regen-bad-owners.sh and regen-duplicate-uuids.sh.
    private static async Task<bool> RewriteJsonFieldAsync(string path, string fieldName, string newValue, CancellationToken ct)
    {
        try
        {
            string json;
            await using (var s = File.OpenRead(path))
            using (var reader = new StreamReader(s))
                json = await reader.ReadToEndAsync(ct);

            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj) return false;
            obj[fieldName] = newValue;

            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), ct);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> ReadFieldAsync(string path, string fieldName, CancellationToken ct)
    {
        try
        {
            await using var s = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(fieldName, out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SetAction(PreflightIssue issue, string newAction)
    {
        // PreflightIssue is a record so the Action property is init-only;
        // we work around by mutating the Details dict to surface the
        // post-fix outcome. The Action field on the record stays at the
        // pre-fix "would-X" value so the JSONL stream remains stable.
        // (Records are by-value records for equality; we'd need to clone
        // to actually change Action, which costs us the bag's reference
        // semantics. The Details dict carries the runtime result.)
        issue.Details ??= new Dictionary<string, object>();
        issue.Details["applied"] = newAction;
    }
}

public sealed record PreflightReport(
    string SourcePath,
    string OutputDir,
    bool DryRun,
    int TotalMetaFiles,
    IReadOnlyList<PreflightIssue> Issues);
