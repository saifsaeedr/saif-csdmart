using Dmart.Models.Api;

namespace Dmart.Api.User.OAuth;

public static class OAuthHandlers
{
    public static void Map(RouteGroupBuilder g)
    {
        // Web OAuth callbacks — Python implements full flows for each.
        // These require OAuth client libraries + provider API credentials.
        g.MapGet("/google/callback", (string? code) =>
            Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "google oauth pending", "request"));
        g.MapGet("/facebook/callback", (string? code) =>
            Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "facebook oauth pending", "request"));
        g.MapGet("/apple/callback", (string? code) =>
            Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "apple oauth pending", "request"));

        // Mobile OAuth — Python validates ID tokens directly from the client.
        // These require provider-specific token validation endpoints:
        //   Google: https://oauth2.googleapis.com/tokeninfo
        //   Facebook: graph.facebook.com/debug_token
        //   Apple: JWKS endpoint for RS256 JWT validation
        g.MapPost("/google/mobile-login", () =>
            Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "google mobile login pending", "request"));
        g.MapPost("/facebook/mobile-login", () =>
            Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "facebook mobile login pending", "request"));
        g.MapPost("/apple/mobile-login", () =>
            Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "apple mobile login pending", "request"));
    }
}
