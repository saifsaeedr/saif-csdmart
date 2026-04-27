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

    public static long UnixSeconds(DateTime t) => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds();
}
