using System.Text.Json;
using Dmart.Auth.OAuth;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
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
//                                      Apple is the exception — it uses POST
//                                      (response_mode=form_post) and 302s the
//                                      browser to AppleOauthCallback?access_
//                                      token=<jwt>, with the same jwt also
//                                      set as the auth_token cookie.
//   POST /{provider}/mobile-login    — mobile: client already has a provider-
//                                      issued token (id_token for Google/
//                                      Apple, access_token for Facebook).
//                                      Body: {"token": "..."}.
//
// Every successful login emits the same response shape as /user/login so any
// SDK that parses password login can handle OAuth too without a second code
// path. The Apple web callback is the one exception: it 302s instead of
// returning JSON, so the cookie is the only signal of success.
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
        })
        .Accepts<Dmart.Models.Api.OAuthMobileLoginBody>("application/json")
        .Produces<Response>()
        .RequireRateLimiting("auth-by-ip");

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
        })
        .Accepts<Dmart.Models.Api.OAuthMobileLoginBody>("application/json")
        .Produces<Response>()
        .RequireRateLimiting("auth-by-ip");

        // ---- Apple ----
        // Apple's web sign-in posts the result back as
        // application/x-www-form-urlencoded (response_mode=form_post) — hence
        // POST, not GET like the other providers. The body carries either:
        //   * id_token  — validate directly, no exchange needed.
        //   * code      — POST to Apple's token endpoint with a freshly-minted
        //                 ES256 client_assertion. Requires the full set of
        //                 TeamId/KeyId/P8/Callback settings; IsConfigured (id
        //                 only) covers the id_token branch.
        // On success we 302 to AppleOauthCallback with `?access_token=<jwt>`
        // appended; the same JWT is also set as the auth_token httponly cookie.
        // On any error we fall back to the JSON 401 ProviderError envelope.
        //
        // SECURITY NOTE: the access_token in the URL is exposed in browser
        // history, the `Referer` header on outbound requests from the
        // landing page, any reverse-proxy / CDN access logs along the
        // way, and the server's own access log. The httponly cookie that
        // CompleteLoginAsync writes is the lower-exposure channel; the
        // URL parameter is only there so a frontend whose origin doesn't
        // share the cookie scope can pick the token up. Frontends SHOULD
        // `history.replaceState({}, '', location.pathname)` to scrub the
        // token from the bar as soon as they've read it.
        g.MapPost("/apple/callback", async (HttpRequest req,
            AppleProvider provider, OAuthUserResolver resolver,
            UserService users, IOptions<DmartSettings> settings,
            HttpContext http, CancellationToken ct) =>
        {
            if (!provider.IsConfigured)
                return ProviderError("apple oauth not configured");

            var redirect = settings.Value.AppleOauthCallback;
            if (string.IsNullOrEmpty(redirect))
                return ProviderError("apple oauth not configured — AppleOauthCallback is empty");

            IFormCollection form;
            try { form = await req.ReadFormAsync(ct); }
            catch { return ProviderError("apple callback body must be application/x-www-form-urlencoded"); }
            var code = form["code"].ToString();
            var idToken = form["id_token"].ToString();

            OAuthUserInfo? info;
            if (!string.IsNullOrEmpty(idToken))
            {
                info = await provider.ValidateIdTokenAsync(idToken, ct);
                if (info is null) return ProviderError("invalid apple id token");
            }
            else if (!string.IsNullOrEmpty(code))
            {
                if (!provider.SupportsCodeExchange)
                    return ProviderError(
                        "apple code exchange not configured — set AppleTeamId, AppleKeyId, AppleP8PrivateKey, AppleOauthCallback");
                info = await provider.ExchangeCodeAsync(code, ct);
                if (info is null) return ProviderError("apple code exchange failed");
            }
            else
            {
                return ProviderError("apple callback needs either `code` or `id_token`");
            }

            // CompleteLoginAsync writes the auth_token cookie and returns the
            // standard /user/login envelope. Pull access_token out of that
            // envelope so we can also expose it as ?access_token=... — the
            // frontend can pick it up from the URL even if the cookie's scope
            // doesn't reach. On error (or any unexpected shape) just forward
            // the IResult so the caller sees the 401 envelope.
            //
            // COUPLED to CompleteLoginAsync's Results.Json envelope shape
            // (records[0].attributes.access_token). If that helper ever
            // returns a different IResult type or a different attribute
            // key, this match falls through to the forward-as-is path and
            // the frontend's URL pickup silently stops working. Keep the
            // two sites updated together.
            var loginResult = await CompleteLoginAsync(info, resolver, users, settings, http, ct);
            if (loginResult is not JsonHttpResult<Response> json
                || json.Value is not { Status: Status.Success, Records: { Count: > 0 } records }
                || !records[0].Attributes!.TryGetValue("access_token", out var accessObj)
                || accessObj is not string access)
            {
                return loginResult;
            }

            var target = QueryHelpers.AddQueryString(redirect, "access_token", access);
            return Results.Redirect(target);
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
        })
        .Accepts<Dmart.Models.Api.OAuthMobileLoginBody>("application/json")
        .Produces<Response>()
        .RequireRateLimiting("auth-by-ip");
    }

    // Resolve-or-create user, issue session, set auth_token cookie, return the
    // same envelope /user/login returns.
    private static async Task<IResult> CompleteLoginAsync(
        OAuthUserInfo info,
        OAuthUserResolver resolver, UserService users,
        IOptions<DmartSettings> settings, HttpContext http, CancellationToken ct)
    {
        var user = await resolver.ResolveAsync(info, ct);

        if (user is null)
        {
            // No dmart account matches this provider id or email. OAuth login no
            // longer auto-creates accounts — the caller turns null into a 401.
            return Results.Json(
                Response.Fail(InternalErrorCode.INVALID_DATA, "Email not found", ErrorTypes.Auth),
                DmartJsonContext.Default.Response, statusCode: 401);
        }

        // Build a synthetic UserLoginRequest so we can flow into the shared
        // ProcessLoginAsync — which handles session row creation, JWT issue,
        // last_login tracking, and max_sessions enforcement.
        var req = new UserLoginRequest(
            Shortname: user.Shortname,
            Email: null, Msisdn: null, Password: null);

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
        var maxAgeSeconds = settings.Value.JwtAccessExpires;
        http.Response.Cookies.Append("auth_token", access, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromSeconds(maxAgeSeconds),
            Path = "/",
        });

        var record = new Record
        {
            ResourceType = Dmart.Models.Enums.ResourceType.User,
            Shortname = loggedIn.Shortname,
            Subpath = "/users",
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
