using System.Text.RegularExpressions;

namespace Dmart.Utils;

// Python-parity regex validation for incoming /managed/request payloads.
//
// Mirrors backend/utils/regex.py from dmart-Python verbatim, including the
// Arabic Unicode ranges (ء-ي letters, ٠-٩ digits,
// ً-ٟ diacritics) so dmart deployments serving Arabic content
// reject the same inputs the Python port rejects.
//
// Used by the request handler to gate Record.shortname / Record.subpath /
// Request.space_name before they touch the dispatcher. A failed match returns
// HTTP 400 with InternalErrorCode.INVALID_DATA, NOT 500 — bad input is the
// caller's problem, not a server crash.
public static class RequestRegex
{
    // Patterns are case-sensitive and anchored — copied byte-for-byte from
    // Python's utils/regex.py. The Arabic ranges:
    //   ء-ي  Arabic letters (ا-ي)
    //   ٠-٩  Arabic-Indic digits (٠-٩)
    //   ً-ٟ  Arabic diacritics (fathah, kasrah, etc.)
    public const string SubpathPattern   = @"^[a-zA-Zء-ي0-9٠-٩ً-ٟ_/]{1,128}$";
    public const string ShortnamePattern = @"^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,64}$";
    public const string SlugPattern      = @"^[a-zA-Z0-9_-]{1,64}$";
    public const string SpaceNamePattern = @"^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,32}$";
    public const string UsernamePattern  = @"^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{3,10}$";
    public const string MsisdnPattern    = @"^[1-9][0-9]{9,14}$";
    public const string OtpCodePattern   = @"^[0-9٠-٩]{6}$";
    // Python's PASSWORD = ^(?=.*[0-9...])(?=.*[A-Z...])[chars]{8,64}$
    public const string PasswordPattern  =
        @"^(?=.*[0-9٠-٩])(?=.*[A-Zء-ي])" +
        @"[a-zA-Zء-ي0-9٠-٩ _#@%*!?$^&()+={}\[\]~|;:,.<>/-]{8,64}$";

    // Compiled once. Regex is allocator-heavy on the hot path — caching the
    // compiled instance lets the request handler validate without re-parsing
    // the pattern for every record.
    private static readonly Regex _shortname  = new(ShortnamePattern,  RegexOptions.Compiled);
    private static readonly Regex _subpath    = new(SubpathPattern,    RegexOptions.Compiled);
    private static readonly Regex _slug       = new(SlugPattern,       RegexOptions.Compiled);
    private static readonly Regex _spaceName  = new(SpaceNamePattern,  RegexOptions.Compiled);
    private static readonly Regex _username   = new(UsernamePattern,   RegexOptions.Compiled);
    private static readonly Regex _msisdn     = new(MsisdnPattern,     RegexOptions.Compiled);
    private static readonly Regex _otp        = new(OtpCodePattern,    RegexOptions.Compiled);
    private static readonly Regex _password   = new(PasswordPattern,   RegexOptions.Compiled);

    public static bool IsValidShortname(string? s) =>
        !string.IsNullOrEmpty(s) && _shortname.IsMatch(s);

    // Subpath gets normalized first: leading `/` is conventional but the
    // pattern matches the trailing-slash-stripped form used internally.
    // "/" alone is allowed (root). Anything else with leading/trailing slashes
    // is normalized via Locator.NormalizeSubpath equivalence.
    public static bool IsValidSubpath(string? s)
    {
        if (s is null) return false;
        if (s == "/" || s == "") return true;  // root
        var trimmed = s.Trim('/');
        return trimmed.Length > 0 && _subpath.IsMatch(trimmed);
    }

    public static bool IsValidSlug(string? s) =>
        !string.IsNullOrEmpty(s) && _slug.IsMatch(s);

    public static bool IsValidSpaceName(string? s) =>
        !string.IsNullOrEmpty(s) && _spaceName.IsMatch(s);

    public static bool IsValidUsername(string? s) =>
        !string.IsNullOrEmpty(s) && _username.IsMatch(s);

    public static bool IsValidMsisdn(string? s) =>
        !string.IsNullOrEmpty(s) && _msisdn.IsMatch(s);

    public static bool IsValidOtpCode(string? s) =>
        !string.IsNullOrEmpty(s) && _otp.IsMatch(s);

    public static bool IsValidPassword(string? s) =>
        !string.IsNullOrEmpty(s) && _password.IsMatch(s);

    // Convenience: which field of the Record/Request triggered the failure?
    // Used by the dispatcher to format a structured error message that's
    // actionable without leaking internal field names that diverge from
    // Python's wire shape.
    public enum Field { Shortname, Subpath, SpaceName, Slug, Msisdn }

    public static string FieldName(Field f) => f switch
    {
        Field.Shortname => "shortname",
        Field.Subpath   => "subpath",
        Field.SpaceName => "space_name",
        Field.Slug      => "slug",
        Field.Msisdn    => "msisdn",
        _               => "unknown",
    };
}
