namespace Dmart.Models.Api;

public sealed record UserLoginRequest(
    string? Shortname,
    string? Email,
    string? Msisdn,
    string? Password,
    string? InvitationToken,
    // OTP code — when non-null, LoginWithOtpAsync is used instead of password auth.
    string? Otp = null,
    // Mobile clients: device identifier + push token.
    string? DeviceId = null,
    string? FirebaseToken = null);

public sealed record SendOTPRequest(
    string? Msisdn,
    string? Email);

public sealed record ConfirmOTPRequest(
    string Code,
    string? Msisdn,
    string? Email);

public sealed record PasswordResetRequest(
    string? Shortname,
    string? Email,
    string? Msisdn);
