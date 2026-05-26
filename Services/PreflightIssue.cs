namespace Dmart.Services;

// One row in the preflight report. Each scanner emits 0..N of these.
// The shape is JSON-serializable for both the per-issue JSONL log and
// the summary file at the end of the run.
//
// `Kind` is a stable string (not an enum) so future scanners can add
// new categories without forcing a model bump. Today's values:
//   "duplicate-uuid"      — two or more meta files share a UUID
//   "missing-owner"       — owner_shortname references a non-existent user
//   "schema-violation"    — payload.body doesn't satisfy its schema
//   "parse-error"         — meta.*.json couldn't be parsed at all
//
// `Severity` controls exit-code policy:
//   "fixable"   — preflight auto-fix can resolve (UUID regen, owner swap)
//   "skip"      — entry will be excluded from import (schema violations)
//   "error"     — unfixable, blocks import (parse errors)
//
// `Action` records what the run actually did. In --dry-run mode this
// is always "would-X"; in default mode it's "X-applied" or "X-failed".
//
// `Details` is free-form per-kind context (e.g. for duplicate-uuid:
// other paths in the dup group; for missing-owner: the unknown
// owner_shortname; for schema-violation: the structured validator
// errors from SchemaValidator.Collect).
public sealed class PreflightIssue
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public required string Severity { get; init; }
    public required string Action { get; init; }
    // Mutable so the auto-fix phase can record post-apply outcomes
    // (e.g. new_uuid, applied="regenerated") without having to clone the
    // whole record. The scan phase always initialises this to a non-null
    // dict (or leaves null for parse-error issues that have no extra
    // context); SetAction lazy-creates it if absent.
    public Dictionary<string, object>? Details { get; set; }
}
