namespace Dmart.Models.Api;

// Mirrors dmart/backend/utils/internal_error_code.py::InternalErrorCode.
// These integer codes appear in Error.code on the wire — every dmart client knows
// them by integer, so any name change MUST keep the integer the same.
public static class InternalErrorCode
{
    // Auth & accounts
    public const int NOT_ALLOWED                  = 401;
    public const int NOT_AUTHENTICATED            = 49;
    public const int INVALID_TOKEN                = 47;
    public const int EXPIRED_TOKEN                = 48;
    public const int SESSION                      = 50;
    public const int INVALID_USERNAME_AND_PASS    = 10;
    public const int USER_ACCOUNT_LOCKED          = 110;
    public const int USER_ISNT_VERIFIED           = 11;
    public const int USERNAME_NOT_EXIST           = 18;
    public const int INVALID_INVITATION           = 125;
    public const int INVALID_PASSWORD_RULES       = 17;
    public const int PASSWORD_NOT_VALIDATED       = 13;
    public const int PASSWORD_RESET_ERROR         = 102;

    // OTP
    public const int OTP_INVALID                  = 307;
    public const int OTP_EXPIRED                  = 308;
    public const int OTP_FAILED                   = 104;
    public const int OTP_ISSUE                    = 100;
    public const int OTP_NEEDED                   = 115;
    public const int OTP_RESEND_BLOCKED           = 103;

    // Identifier / data
    public const int INVALID_IDENTIFIER           = 420;
    public const int INVALID_CONFIRMATION         = 427;
    public const int SHORTNAME_ALREADY_EXIST      = 400;
    public const int SHORTNAME_DOES_NOT_EXIST     = 404;
    public const int INVALID_DATA                 = 402;
    public const int INVALID_STANDALONE_DATA      = 107;
    public const int ONE_ARGUMENT_ALLOWED         = 101;
    public const int DATA_SHOULD_BE_UNIQUE        = 415;
    public const int MISSING_DATA                 = 202;
    public const int MISSING_METADATA             = 208;
    public const int MISSING_FILTER_SHORTNAMES    = 209;
    public const int MISSING_DESTINATION_OR_SHORTNAME = 213;
    public const int EMAIL_OR_MSISDN_REQUIRED     = 207;
    public const int UNMATCHED_DATA               = 19;
    public const int UNPROCESSABLE_ENTITY         = 424;

    // Entries / spaces / paths
    public const int OBJECT_NOT_FOUND             = 220;
    public const int OBJECT_NOT_SAVED             = 51;
    public const int INVALID_SPACE_NAME           = 203;
    public const int ALREADY_EXIST_SPACE_NAME     = 205;
    public const int CANNT_DELETE                 = 204;
    public const int NOT_ALLOWED_LOCATION         = 206;
    public const int PROVID_SOURCE_PATH           = 222;
    public const int DIR_NOT_FOUND                = 22;
    public const int INVALID_ROUTE                = 230;
    public const int PROTECTED_FIELD              = 210;
    public const int NOT_SUPPORTED_TYPE           = 217;
    public const int SOME_SUPPORTED_TYPE          = 219;
    public const int WORKFLOW_BODY_NOT_FOUND      = 218;

    // Tickets
    public const int TICKET_ALREADY_CLOSED        = 299;
    public const int INVALID_TICKET_STATUS        = 300;

    // Locks
    public const int LOCK_UNAVAILABLE             = 30;
    public const int LOCKED_ENTRY                 = 31;

    // QR
    public const int QR_ERROR                     = 14;
    public const int QR_EXPIRED                   = 15;
    public const int QR_INVALID                   = 16;

    // jq
    public const int JQ_TIMEOUT                   = 120;
    public const int JQ_ERROR                     = 121;

    // Misc / catch-all
    public const int CONFLICT                     = 409;
    public const int SOMETHING_WRONG              = 430;
    public const int INVALID_HEALTH_CHECK         = 403;
    public const int INVALID_APP_KEY              = 555;
}
