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

            // Route dispatch — invitation takes precedence over OTP over password.
            // Matches Python's `/user/login` path-selection order in user/router.py.
            var result = !string.IsNullOrEmpty(req.Invitation)
                ? await svc.LoginWithInvitationAsync(req, headers, ct)
                : !string.IsNullOrEmpty(req.Otp)
                    ? await svc.LoginWithOtpAsync(req, headers, ct)
                    : await svc.LoginAsync(req, headers, ct);

            if (!result.IsOk)
                return Results.Json(
                    Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "auth"),
                    DmartJsonContext.Default.Response, statusCode: 401);

            var (access, refresh, user) = result.Value;

            // dmart sets an httponly cookie called auth_token in addition to returning
            // the token in the body. Browser clients rely on the cookie.
            var maxAgeSeconds = settings.Value.JwtAccessMinutes * 60;
            http.Response.Cookies.Append("auth_token", access, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.FromSeconds(maxAgeSeconds),
                Path = "/",
            });

            // Python's process_user_login returns:
            //   Response(status=success, records=[Record(
            //     resource_type="user", shortname=user.shortname,
            //     attributes={access_token, type, displayname?})])
            // We mirror that plus include refresh_token and roles for parity
            // with clients that already depend on those being in the response.
            var loginRecord = new Record
            {
                ResourceType = Dmart.Models.Enums.ResourceType.User,
                Shortname = user.Shortname,
                Subpath = "users",
                Attributes = new()
                {
                    ["access_token"] = access,
                    ["refresh_token"] = refresh,
                    ["type"] = user.Type.ToString().ToLowerInvariant(),
                    ["roles"] = user.Roles,
                },
            };
            if (user.Displayname is not null)
                loginRecord.Attributes["displayname"] = user.Displayname;

            return Results.Json(Response.Ok(new[] { loginRecord }),
                DmartJsonContext.Default.Response);
        }).RequireRateLimiting("auth-by-ip");

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
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.Zero,
                Path = "/",
            });
            return Response.Ok();
        });
    }
}
