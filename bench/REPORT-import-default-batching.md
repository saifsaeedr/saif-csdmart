# Import: default path batching vs --fast (2026-06-10)

## What changed

The default (non `--fast`) import path historically wrote one row per
connection via `EntryRepository.UpsertAsync` — measured at ~216 rows/sec on a
real 133k-row import (2026-05-18). As of this change, every import runs the
same binary COPY + temp-table merge batching as `--fast`, on a session that
keeps FK constraints and triggers enforced. `--fast` now differs only in:

- `session_replication_role='replica'` (FK/trigger bypass — trusted source)
- per-space/sub-shard parallelism (`--fast-parallelism`)

A default-path batch that trips an integrity error (unknown owner → FK
violation at the deferred commit; duplicate uuid → PK violation at the merge)
is replayed row-by-row so the offenders fail alone into the failure sidecar —
the per-row semantics the default path always had. Covered by
`ImportBadRowIsolationTests` (pinned against the pre-change importer first).

History lines now ride the session connection inside per-line savepoints
(previously: one connection per line on the default path; on the fast path a
single bad line could poison the shard's trailing transaction).

## Measurement

Fixture: 20,000 content entries (~100-byte JSON payload each), one space,
10 subpaths, import-canonical layout (`{space}/{subpath}/.dm/{sn}/meta.content.json`).
Host: localhost PostgreSQL, NVMe, same machine as importer. Empty
`dmart_scratch` DB (admin row present). `--no-validate`, `--type=fs`.
Wall time includes process start (~0.5 s) and the filesystem walk.

- Default path (batched, FKs enforced): 2.76 s → ~7,200 rows/sec wall
  (single batch flush of 10k default size, all 20,000 rows landed)
- `--fast` (replica role, serial — one space → one shard): 2.95 s → ~6,800 rows/sec
- Old default path (per-row, for reference): ~216 rows/sec measured 2026-05-18
  → 20k rows would have taken ~93 s

Difference between the two paths on clean single-space data is noise. The
remaining `--fast` value is (a) multi-space/sub-shard parallelism at large
scale and (b) importing data with intentionally dangling references.

Note: `bench/gen_fixture.py` output is NOT import-compatible (it produces
folder-style `{sn}/.dm/meta.json` layout for content entries — fine for the
preflight walk benches it was built for, rejected by `dmart import`'s path
parser). The fixture for this report was generated inline; see the layout
above if reproducing.
