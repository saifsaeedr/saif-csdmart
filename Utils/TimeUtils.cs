namespace Dmart.Utils;

public static class TimeUtils
{
    // Local wall-clock now. Mirrors Python dmart's `datetime.now()` (naive
    // local) for entity timestamps, log timestamps, OTP/session TTL stamps,
    // and the x-server-time header. The DB columns are TIMESTAMP (without
    // time zone) so a Kind=Local DateTime writes verbatim under Npgsql legacy
    // timestamp behavior; reads return Kind=Unspecified with the same value.
    // No timezone conversion anywhere in the system.
    public static DateTime Now() => DateTime.Now;

    // Strip any timezone marker so the value writes verbatim to a TIMESTAMP
    // (without time zone) column. dmart is timezone-less end to end, so a
    // Kind=Utc value parsed from a "…Z" source meta must be stored as the
    // same wall-clock — never tz-converted. SpecifyKind keeps the clock
    // components and only relabels the Kind, which `NpgsqlDbType.Timestamp`
    // accepts under both legacy and modern Npgsql timestamp behavior.
    public static DateTime Naive(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Unspecified);

    public static long UnixSeconds(DateTime t) => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds();
}
