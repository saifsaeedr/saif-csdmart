namespace Dmart.Client;

// Strongly-typed configuration for DmartClient — same shape pattern as
// Dmart.SqlAdapter's DmartDbOptions, so a project that already binds
// IConfiguration to a "Dmart" section gets free env-var support
// (`Dmart__BaseUrl`, `Dmart__AuthToken`, …) via the standard ASP.NET
// configuration pipeline.
//
// For non-DI scenarios where the consumer just wants "set DMART_BASE_URL
// and forget", ResolveBaseUrl() falls back to the DMART_BASE_URL env var
// when BaseUrl isn't set. That covers the simplest deployment case
// without forcing every caller to wire up IConfiguration first.
public sealed class DmartClientOptions
{
    // Required (or supply via DMART_BASE_URL env var). Trailing slashes are
    // stripped during ResolveBaseUrl so callers don't have to be careful.
    public string? BaseUrl { get; set; }

    // Optional bearer token to attach to every request. Useful for
    // service-to-service calls where the token is provisioned out-of-band
    // (a service-account token from a secret manager, e.g.). Callers that
    // log in with LoginAsync don't need to set this.
    public string? AuthToken { get; set; }

    // Optional HttpClient.Timeout override. Only honored on the ctor that
    // owns its HttpClient — when the consumer supplies their own
    // HttpClient, we leave the timeout alone so we don't trample DI
    // configuration the consumer already set.
    public TimeSpan? Timeout { get; set; }

    // Optional headers added to every request. Use sparingly — most
    // consumers can rely on the bearer token alone. Headers set here win
    // over DefaultRequestHeaders on the supplied HttpClient.
    public Dictionary<string, string> DefaultHeaders { get; set; } = new(StringComparer.Ordinal);

    // Resolves the effective base URL: explicit BaseUrl wins, otherwise
    // fall back to DMART_BASE_URL env var. Throws if neither is set so
    // the failure surfaces at construction (loud) instead of on the first
    // request (mysterious 404 against the empty string).
    public string ResolveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl)) return BaseUrl!.TrimEnd('/');
        var envUrl = Environment.GetEnvironmentVariable("DMART_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envUrl)) return envUrl!.TrimEnd('/');
        throw new InvalidOperationException(
            "DmartClient base URL is not configured. Set DmartClientOptions.BaseUrl, " +
            "the DMART_BASE_URL environment variable, or bind IConfiguration to a " +
            "Dmart section (Dmart__BaseUrl in env-var form).");
    }
}
