using Microsoft.Extensions.Options;

namespace Dmart.Config;

// Validates DmartSettings at startup. A misconfiguration (negative port, zero
// pool size, empty DB host, etc.) should fail the process loudly rather than
// producing obscure runtime errors later. Hooked up via
// builder.Services.AddSingleton<IValidateOptions<DmartSettings>, DmartSettingsValidator>()
// plus .ValidateOnStart() on the options registration.
internal sealed class DmartSettingsValidator : IValidateOptions<DmartSettings>
{
    public ValidateOptionsResult Validate(string? name, DmartSettings s)
    {
        var failures = new List<string>();

        if (s.ListeningPort is < 1 or > 65535)
            failures.Add($"ListeningPort must be 1-65535 (got {s.ListeningPort})");
        if (s.DatabasePort is < 1 or > 65535)
            failures.Add($"DatabasePort must be 1-65535 (got {s.DatabasePort})");
        if (s.DatabasePoolSize <= 0)
            failures.Add($"DatabasePoolSize must be > 0 (got {s.DatabasePoolSize})");
        if (s.DatabasePoolTimeout <= 0)
            failures.Add($"DatabasePoolTimeout must be > 0 (got {s.DatabasePoolTimeout})");
        if (s.DatabaseMaxOverflow < 0)
            failures.Add($"DatabaseMaxOverflow must be >= 0 (got {s.DatabaseMaxOverflow})");
        if (string.IsNullOrWhiteSpace(s.DatabaseHost) && string.IsNullOrWhiteSpace(s.PostgresConnection))
            failures.Add("DatabaseHost (or PostgresConnection) must be configured");
        if (string.IsNullOrWhiteSpace(s.DatabaseName) && string.IsNullOrWhiteSpace(s.PostgresConnection))
            failures.Add("DatabaseName (or PostgresConnection) must be configured");
        if (s.JwtAccessMinutes <= 0)
            failures.Add($"JwtAccessMinutes must be > 0 (got {s.JwtAccessMinutes})");
        if (s.JwtRefreshDays <= 0)
            failures.Add($"JwtRefreshDays must be > 0 (got {s.JwtRefreshDays})");
        if (s.JwtInvitationDays <= 0)
            failures.Add($"JwtInvitationDays must be > 0 (got {s.JwtInvitationDays})");
        if (string.IsNullOrWhiteSpace(s.JwtSecret) || s.JwtSecret.Length < 32)
            failures.Add("JwtSecret must be at least 32 bytes (HS256 signing key)");
        if (s.MaxFailedLoginAttempts < 0)
            failures.Add($"MaxFailedLoginAttempts must be >= 0 (got {s.MaxFailedLoginAttempts})");
        if (s.MaxQueryLimit < 1)
            failures.Add($"MaxQueryLimit must be >= 1 (got {s.MaxQueryLimit})");
        if (s.RequestTimeout <= 0)
            failures.Add($"RequestTimeout must be > 0 (got {s.RequestTimeout})");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
