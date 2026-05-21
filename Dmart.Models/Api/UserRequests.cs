namespace Dmart.Models.Api;

public sealed record UserLoginRequest(
    string? Shortname,
    string? Email,
    string? Msisdn,
    string? Password,
    // Invitation JWT — Python wire format: `"invitation"` in the JSON body.
    // When non-null, LoginWithInvitationAsync is used instead of password/OTP.
    string? Invitation,
    // OTP code — when non-null, LoginWithOtpAsync is used instead of password auth.
    string? Otp = null,
    // Mobile clients: device identifier + push token.
    string? DeviceId = null,
    string? FirebaseToken = null);

public sealed record SendOTPRequest(
    string? Msisdn,
    string? Email,
    string? Shortname = null);

public sealed record ConfirmOTPRequest(
    string Code,
    string? Msisdn,
    string? Email);

public sealed record PasswordResetRequest(
    string? Shortname,
    string? Email,
    string? Msisdn);

// POST /user/password-reset-confirm — completes the flow started by
// /user/password-reset-request. Identifier is one of {Shortname, Email,
// Msisdn} — same shape as PasswordResetRequest so the two halves resolve to
// the same user (and the same `pwd-reset:{dest}` key) without heuristic
// re-detection of identifier shape. Otp is the code delivered to the user;
// Password is the new password (plaintext on the wire, hashed server-side
// via PasswordHasher.Hash).
public sealed record PasswordResetConfirm(
    string? Shortname,
    string? Email,
    string? Msisdn,
    string Otp,
    string Password);

// RFC 7591 dynamic client registration — MCP clients post this to /oauth/register
// and we echo back a clients_id they use for the authorize+token flow.
public sealed record RegisterRequest(
    List<string>? RedirectUris,
    string? ClientName,
    // Fields the spec allows clients to send but we don't honor on public
    // clients — accepted for compatibility with Claude Desktop / Cursor
    // registration bodies, but ignored.
    string? TokenEndpointAuthMethod = null,
    List<string>? GrantTypes = null,
    List<string>? ResponseTypes = null);

public sealed record RegisterResponse(
    string ClientId,
    string ClientName,
    List<string> RedirectUris,
    string TokenEndpointAuthMethod,
    List<string> GrantTypes,
    List<string> ResponseTypes);
