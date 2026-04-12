using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.User;

public static class AuthHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/login", async Task<IResult> (
            UserLoginRequest req,
            UserService svc,
            HttpContext http,
            IOptions<DmartSettings> settings,
            CancellationToken ct) =>
        {
            // Build stripped request headers for last_login tracking (Python removes
            // authorization and cookie headers before persisting).
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in http.Request.Headers)
            {
                if (string.Equals(h.Key, "authorization", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(h.Key, "cookie", StringComparison.OrdinalIgnoreCase)) continue;
                headers[h.Key] = h.Value.ToString();
            }

            // Route to OTP or password login based on whether otp field is present.
            var result = !string.IsNullOrEmpty(req.Otp)
                ? await svc.LoginWithOtpAsync(req, headers, ct)
                : await svc.LoginAsync(req, headers, ct);

            if (!result.IsOk)
                return Results.Json(Response.Fail(result.ErrorCode!, result.ErrorMessage!, type: "auth"),
                    DmartJsonContext.Default.Response, statusCode: 401);

            var (access, refresh, user) = result.Value;

            // dmart sets an httponly cookie called auth_token in addition to returning
            // the token in the body. Browser clients rely on the cookie.
            var maxAgeSeconds = settings.Value.JwtAccessMinutes * 60;
            http.Response.Cookies.Append("auth_token", access, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromSeconds(maxAgeSeconds),
                Path = "/",
            });

            return Results.Json(Response.Ok(attributes: new()
            {
                ["access_token"] = access,
                ["refresh_token"] = refresh,
                ["shortname"] = user.Shortname,
                ["roles"] = user.Roles,
                ["type"] = user.Type.ToString().ToLowerInvariant(),
            }), DmartJsonContext.Default.Response);
        });

        g.MapPost("/logout", async Task<Response> (HttpContext http, UserService svc, CancellationToken ct) =>
        {
            // Python: db.remove_user_session() — delete the session row.
            var token = http.Request.Cookies["auth_token"];
            await svc.LogoutAsync(token, ct);

            // Clear the cookie by setting an empty value with max_age=0.
            http.Response.Cookies.Append("auth_token", "", new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.Zero,
                Path = "/",
            });
            return Response.Ok();
        });
    }
}
