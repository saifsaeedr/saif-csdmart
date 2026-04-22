using System.Text.Json;
using Dmart.Auth.OAuth;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.User.OAuth;

// HTTP surface for social login (Google / Facebook / Apple). Two flows per
// provider, matching Python:
//
//   GET  /{provider}/callback        — web: `?code=<auth_code>`, we exchange
//                                      the code with the provider, validate
//                                      the resulting id_token / profile,
//                                      resolve the user, and return the
//                                      standard login response (access_token
//                                      in records[0].attributes + auth_token
//                                      cookie).
//   POST /{provider}/mobile-login    — mobile: client already has a provider-
//                                      issued token (id_token for Google/
//                                      Apple, access_token for Facebook).
//                                      Body: {"token": "..."}.
//
// Every successful login emits the same response shape as /user/login so any
// SDK that parses password login can handle OAuth too without a second code
// path.
public static class OAuthHandlers
{
    public static void Map(RouteGroupBuilder g)
    {
        // ---- Google ----
        g.MapGet("/google/callback", async (string? code,
            GoogleProvider provider, OAuthUserResolver resolver,
            UserService users, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(code))
                return ProviderError("missing `code` query parameter");
            if (!provider.IsConfigured)
                return ProviderError("google oauth not configured");
            var idToken = await provider.ExchangeCodeForIdTokenAsync(code, ct);
            if (idToken is null) return ProviderError("google code exchange failed");
            var info = await provider.ValidateIdTokenAsync(idToken, ct);
            if (info is null) return ProviderError("invalid google id token");
            return await CompleteLoginAsync(info, resolver, users, settings, http, ct);
        });

        g.MapPost("/google/mobile-login", async (HttpRequest req,
            GoogleProvider provider, OAuthUserResolver resolver,
            UserService users, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            if (!provider.IsConfigured)
                return ProviderError("google oauth not configured");
            var token = await ExtractTokenAsync(req, ct);
            if (string.IsNullOrEmpty(token)) return ProviderError("missing `token` in body");
            var info = await provider.ValidateIdTokenAsync(token, ct);
            if (info is null) return ProviderError("invalid google id token");
            return await CompleteLoginAsync(info, resolver, users, settings, http, ct);
        }).RequireRateLimiting("auth-by-ip");

        // ---- Facebook ----
        g.MapGet("/facebook/callback", async (string? code,
            FacebookProvider provider, OAuthUserResolver resolver,
            UserService users, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(code))
                return ProviderError("missing `code` query parameter");
            if (!provider.IsConfigured)
                return ProviderError("facebook oauth not configured");
            var access = await provider.ExchangeCodeForAccessTokenAsync(code, ct);
            if (access is null) return ProviderError("facebook code exchange failed");
            var info = await provider.ValidateAccessTokenAsync(access, ct);
            if (info is null) return ProviderError("invalid facebook access token");
            return await CompleteLoginAsync(info, resolver, users, settings, http, ct);
        });

        g.MapPost("/facebook/mobile-login", async (HttpRequest req,
            FacebookProvider provider, OAuthUserResolver resolver,
            UserService users, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            if (!provider.IsConfigured)
                return ProviderError("facebook oauth not configured");
            var token = await ExtractTokenAsync(req, ct);
            if (string.IsNullOrEmpty(token)) return ProviderError("missing `token` in body");
            var info = await provider.ValidateAccessTokenAsync(token, ct);
            if (info is null) return ProviderError("invalid facebook access token");
            return await CompleteLoginAsync(info, resolver, users, settings, http, ct);
        }).RequireRateLimiting("auth-by-ip");

        // ---- Apple ----
        // Apple's web-callback code exchange needs a signed client-assertion
        // JWT (team id + key id + ES256 private key) — that's heavy and
        // rarely used vs the mobile flow. We implement it only when
        // AppleClientSecretPrivateKey is configured; otherwise clean error.
        g.MapGet("/apple/callback", async (string? code, string? id_token,
            AppleProvider provider, OAuthUserResolver resolver,
            UserService users, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            if (!provider.IsConfigured)
                return ProviderError("apple oauth not configured");

            // Apple sometimes posts id_token directly in the callback (form
            // post response mode). If we have it, skip the code exchange.
            string? token = id_token;
            if (string.IsNullOrEmpty(token))
            {
                if (string.IsNullOrEmpty(code))
                    return ProviderError("apple callback needs either `code` or `id_token`");
                // Code → id_token exchange requires client_secret JWT — not
                // implemented for AOT-simplicity reasons. Mobile flow covers
                // the 95% case. See AppleProvider config comments.
                return ProviderError(
                    "apple code exchange requires client_secret JWT signing — " +
                    "configure AppleTeamId/AppleKeyId/AppleClientSecretPrivateKey or use the mobile flow");
            }
            var info = await provider.ValidateIdTokenAsync(token, ct);
            if (info is null) return ProviderError("invalid apple id token");
            return await CompleteLoginAsync(info, resolver, users, settings, http, ct);
        });

        g.MapPost("/apple/mobile-login", async (HttpRequest req,
            AppleProvider provider, OAuthUserResolver resolver,
            UserService users, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            if (!provider.IsConfigured)
                return ProviderError("apple oauth not configured");
            var token = await ExtractTokenAsync(req, ct);
            if (string.IsNullOrEmpty(token)) return ProviderError("missing `token` in body");
            var info = await provider.ValidateIdTokenAsync(token, ct);
            if (info is null) return ProviderError("invalid apple id token");
            return await CompleteLoginAsync(info, resolver, users, settings, http, ct);
        }).RequireRateLimiting("auth-by-ip");
    }

    // Resolve-or-create user, issue session, set auth_token cookie, return the
    // same envelope /user/login returns.
    private static async Task<IResult> CompleteLoginAsync(
        OAuthUserInfo info,
        OAuthUserResolver resolver, UserService users,
        IOptions<DmartSettings> settings, HttpContext http, CancellationToken ct)
    {
        var user = await resolver.ResolveAsync(info, ct);

        // Build a synthetic UserLoginRequest so we can flow into the shared
        // ProcessLoginAsync — which handles session row creation, JWT issue,
        // last_login tracking, and max_sessions enforcement.
        var req = new UserLoginRequest(
            Shortname: user.Shortname,
            Email: null, Msisdn: null, Password: null, Invitation: null);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in http.Request.Headers)
        {
            if (string.Equals(h.Key, "authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(h.Key, "cookie", StringComparison.OrdinalIgnoreCase)) continue;
            headers[h.Key] = h.Value.ToString();
        }

        var result = await users.ProcessLoginAsync(user, req, headers, ct);
        if (!result.IsOk)
            return Results.Json(Response.Fail(result.ErrorCode, result.ErrorMessage!,
                result.ErrorType ?? "auth"),
                DmartJsonContext.Default.Response, statusCode: 401);

        // Python-parity: login emits a single long-lived access token; the
        // refresh minted by ProcessLoginAsync is discarded here. MCP OAuth
        // clients that need refresh go through /oauth/token directly.
        var (access, _, loggedIn) = result.Value;

        // Match /user/login's cookie: httponly auth_token, same-site strict.
        var maxAgeSeconds = settings.Value.JwtAccessMinutes * 60;
        http.Response.Cookies.Append("auth_token", access, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromSeconds(maxAgeSeconds),
            Path = "/",
        });

        var record = new Record
        {
            ResourceType = Dmart.Models.Enums.ResourceType.User,
            Shortname = loggedIn.Shortname,
            Subpath = "users",
            Attributes = new()
            {
                ["access_token"] = access,
                ["type"] = loggedIn.Type.ToString().ToLowerInvariant(),
                ["roles"] = loggedIn.Roles,
            },
        };
        if (loggedIn.Displayname is not null)
            record.Attributes["displayname"] = loggedIn.Displayname;

        return Results.Json(Response.Ok(new[] { record }),
            DmartJsonContext.Default.Response);
    }

    private static async Task<string?> ExtractTokenAsync(HttpRequest req, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("token", out var t) &&
                   t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch { return null; }
    }

    private static IResult ProviderError(string message) =>
        Results.Json(
            Response.Fail(InternalErrorCode.INVALID_DATA, message, ErrorTypes.Auth),
            DmartJsonContext.Default.Response, statusCode: 401);
}
