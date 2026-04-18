using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.User;

public static class RegistrationHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Python parity: POST /user/create takes a core.Record body
        //   {shortname, subpath, resource_type, attributes:{email, msisdn,
        //    password, email_otp, msisdn_otp, roles, displayname, description,
        //    payload:{content_type, body}, ...}}
        // and returns a Record with session attributes (access_token, type,
        // displayname?) after auto-logging the user in.
        g.MapPost("/create", async Task<IResult> (HttpContext http, UserService svc,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            Record? record;
            try
            {
                record = await JsonSerializer.DeserializeAsync(
                    http.Request.Body, DmartJsonContext.Default.Record, ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(
                    Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, "request"),
                    DmartJsonContext.Default.Response, statusCode: 400);
            }
            if (record is null)
                return Results.Json(
                    Response.Fail(InternalErrorCode.INVALID_DATA, "missing body", "request"),
                    DmartJsonContext.Default.Response, statusCode: 400);

            // Python strips authorization/cookie from request_headers before
            // persisting last_login. Matches process_user_login behaviour.
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in http.Request.Headers)
            {
                if (string.Equals(h.Key, "authorization", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(h.Key, "cookie", StringComparison.OrdinalIgnoreCase)) continue;
                headers[h.Key] = h.Value.ToString();
            }

            var result = await svc.CreateAsync(record, headers, ct);
            if (!result.IsOk)
                return Results.Json(
                    Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "create"),
                    DmartJsonContext.Default.Response,
                    statusCode: FailedResponseFilter.MapErrorToHttpStatus(result.ErrorCode));

            var (user, access, _) = result.Value;

            // Set auth_token cookie — mirrors the login flow so browser
            // clients are authenticated immediately after create.
            var maxAgeSeconds = settings.Value.JwtAccessMinutes * 60;
            http.Response.Cookies.Append("auth_token", access, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.FromSeconds(maxAgeSeconds),
                Path = "/",
            });

            var responseRecord = new Record
            {
                ResourceType = Dmart.Models.Enums.ResourceType.User,
                Shortname = user.Shortname,
                Subpath = "users",
                Attributes = new()
                {
                    ["access_token"] = access,
                    ["type"] = user.Type.ToString().ToLowerInvariant(),
                },
            };
            if (user.Displayname is not null)
                responseRecord.Attributes["displayname"] = user.Displayname;

            return Results.Json(Response.Ok(new[] { responseRecord }),
                DmartJsonContext.Default.Response);
        });
    }
}
