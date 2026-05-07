namespace Dmart.Models.Api;

// Closed set of wire-format values that appear in api.Error.Type —
// the "type" field in every failure response envelope. Clients
// branch on these exact strings (tsdmart, pydmart, dmart-cli), so
// the constants preserve byte-for-byte compatibility while giving
// compile-time typo protection and a central place to document each
// meaning.
//
// Values enumerated from audit of Response.Fail / Result.Fail call
// sites across the codebase. If a new wire value is introduced,
// add it here and port any integration test that asserts on the
// string (grep for the literal).
public static class ErrorTypes
{
    // Login/session/token failures — reserved for the authentication layer.
    public const string Auth = "auth";

    // JWT-specific failure (invitation tokens, malformed bearer tokens,
    // old_password mismatch on profile updates) — Python distinguishes
    // this from "auth" because different clients route it differently.
    public const string JwtAuth = "jwtauth";

    // Database-layer failure (conflicts, unique violations, missing FKs).
    public const string Db = "db";

    // Caller supplied bad input (bad body, missing fields, invalid
    // route params) — this is the default and most common type.
    public const string Request = "request";

    // User registration-flow failure (Python's /user/create path).
    public const string Create = "create";

    // Unhandled server-side exception made it to the boundary.
    public const string Exception = "exception";

    // Caller tried to write a protected field (payload.body keys enumerated
    // in settings.UserProfilePayloadProtectedFields). Python emits this
    // under HTTP 401 with type="restriction" — we preserve the string.
    public const string Restriction = "restriction";

    // Internal-consistency violation (dmart bug, not client fault).
    public const string Internal = "internal";

    // Media/attachment-specific failure (missing entry, bad bytes).
    public const string Media = "media";

    // Attachment-write failure (DB insert into attachments table).
    public const string Attachment = "attachment";

    // QR-code generator endpoint failure.
    public const string Qr = "qr";

    // Channel-authentication gate (utils/middleware.py::ChannelMiddleware in
    // Python). Surfaced when ENABLE_CHANNEL_AUTH=true and the request either
    // omits the x-channel-key header for a channel-restricted path or supplies
    // a key that doesn't grant access to the requested path.
    public const string ChannelAuth = "channel_auth";
}
