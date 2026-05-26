namespace Dmart.Cli;

// Parsed argv shape for `dmart preflight`. Mirrors the SelfCheckOptions
// record style — record types are cheap, immutable, and let the
// command's Run wrapper hand a single object down to the service layer.
//
// `DryRun` is the only mode flag: default is false (auto-fix in place,
// per the v0.9.26 design). The three scanners (UUIDs / owners / schema)
// always run; the difference is whether their repair actions execute.
//
// `Workers` clamps the per-space parallelism on the filesystem walk —
// I/O bound at this scale, so the realistic ceiling is
// Environment.ProcessorCount. Operators tuning for spinning disks may
// want lower; SSDs / NVMe can go higher.
public sealed record PreflightOptions(
    string Path,
    bool DryRun,
    int Workers,
    string? OutputDir,
    int Sample,
    bool Verbose);
