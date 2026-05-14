using System.Text.Json;

namespace Dmart.SqlAdapter;

// Adapter-wide tuning. Anything that should NOT be a per-call argument
// because the value must be consistent across concurrent callers (the lock
// period being the canonical example) lives here.
//
// dmart's server reads its lock period from a single global `DmartSettings.LockPeriod`
// — every caller agrees on the threshold for "this lock is stale". The
// SDK mirrors that contract with a single value baked into the adapter at
// construction time. Per-call lock-period arguments on TryLockAsync /
// GetLockerAsync are kept for signature parity with the HTTP-shaped Client
// SDK but are NOT honored — the adapter-wide value wins.
public sealed class DmartSqlAdapterOptions
{
    // Default matches dmart Python's settings.LockPeriod default of 300s.
    // Lock rows older than this are treated as expired (purgeable) by
    // TryLockAsync and invisible to GetLockerAsync.
    public int LockPeriodSeconds { get; set; } = 300;

    // Optional override of the JSON options used for JSONB read/write.
    // When null the adapter builds the snake_case-lower / ignore-when-null
    // options that match dmart's wire format.
    public JsonSerializerOptions? JsonOptions { get; set; }
}
