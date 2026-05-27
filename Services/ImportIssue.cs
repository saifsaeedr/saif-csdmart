namespace Dmart.Services;

// One row in the import-time validation report. Each per-entry validator
// emits 0..N of these. The shape matches PreflightIssue conceptually but
// reflects a key difference: import-time validation NEVER mutates source
// files. Auto-fixes (owner remap, uuid regen) happen in memory only and
// are reflected in PG; the source meta files stay byte-for-byte identical.
//
// Stable string Kind so future validators can add categories without
// model bumps. Today's values:
//   "parse-error"        — meta.*.json couldn't be parsed; entry skipped
//   "owner-remapped"     — owner_shortname unknown; in-memory swap to "dmart"
//   "uuid-regenerated"   — uuid collided with an earlier entry or PG row;
//                          in-memory regen to a fresh Guid
//   "schema-violation"   — body failed JSON Schema validation; entry skipped
//
// Action records the in-memory outcome:
//   "skipped"            — entry was not inserted into PG
//   "fixed-in-memory"    — entry was modified before insert (owner / uuid)
public sealed class ImportIssue
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public required string Action { get; init; }
    public Dictionary<string, object>? Details { get; set; }
}
