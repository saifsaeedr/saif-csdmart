using System.Text.Json;
using Dmart.Api.Managed;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Plugins;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.User;

public static class RegistrationHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // POST /user/create — self-registration. The wire shape is
        //   {attributes:{email, msisdn, password, email_otp, msisdn_otp,
        //    roles, displayname, description, payload:{content_type, body},
        //    ...}}
        // and returns a Record with session attributes (access_token, type,
        // displayname?) after auto-logging the user in. Shortname + uuid
        // are server-allocated and intentionally not part of the request
        // shape — this is a deliberate divergence from Python parity (which
        // accepts caller-supplied shortnames) so that no caller can squat
        // on a name on this public endpoint.
        g.MapPost("/create", async Task<IResult> (HttpContext http, UserService svc,
            PluginManager plugins, IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            UserCreateBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(
                    http.Request.Body, DmartJsonContext.Default.UserCreateBody, ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(
                    Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request),
                    DmartJsonContext.Default.Response, statusCode: 400);
            }
            if (body is null)
                return Results.Json(
                    Response.Fail(InternalErrorCode.INVALID_DATA, "missing body", ErrorTypes.Request),
                    DmartJsonContext.Default.Response, statusCode: 400);

            // Build the internal Record server-side. Shortname is sent as
            // the "auto" sentinel so ResolveAutoShortname mints a fresh
            // UUID-derived 8-char value, matching every other create path.
            var record = new Record
            {
                ResourceType = ResourceType.User,
                Shortname = "auto",
                Subpath = "/",
                Attributes = body.Attributes,
            };

            // Python strips authorization/cookie from request_headers before
            // persisting last_login. Matches process_user_login behaviour.
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in http.Request.Headers)
            {
                if (string.Equals(h.Key, "authorization", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(h.Key, "cookie", StringComparison.OrdinalIgnoreCase)) continue;
                headers[h.Key] = h.Value.ToString();
            }

            // The shortname is the server-minted "auto" sentinel; retry past the
            // rare 8-hex collision (re-minting each attempt via ResolveAutoShortname
            // inside the loop) instead of surfacing it to the registrant.
            var result = await RequestHandler.RetryOnShortnameCollisionAsync(
                RequestHandler.IsAutoShortname(record.Shortname),
                () => svc.CreateAsync(RequestHandler.ResolveAutoShortname(record), headers, ct),
                r => r.ErrorCode == InternalErrorCode.SHORTNAME_ALREADY_EXIST);
            if (!result.IsOk)
                return Results.Json(
                    Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "create"),
                    DmartJsonContext.Default.Response,
                    statusCode: FailedResponseFilter.MapErrorToHttpStatus(result.ErrorCode));

            var (user, access, _) = result.Value;

            // Notify plugin hooks of the new user, same as the managed
            // CRUD path (RequestHandler.cs:174-192) and EntryService
            // (EntryService.cs:133). Following the EntryService pattern:
            // call AfterActionAsync directly so plugin authors' Concurrent
            // flag is honored — synchronous hooks block, Concurrent hooks
            // run fire-and-forget. PluginManager logs failures itself
            // (Plugins/PluginManager.cs:252,264) and after-hook errors
            // never fail the originating action.
            await plugins.AfterActionAsync(new Event
            {
                SpaceName = settings.Value.ManagementSpace,
                Subpath = "/users",
                Shortname = user.Shortname,
                ActionType = ActionType.Create,
                ResourceType = ResourceType.User,
                // Self-registration: the actor is the user being created.
                UserShortname = user.Shortname,
            }, ct);

            // Set auth_token cookie — mirrors the login flow so browser
            // clients are authenticated immediately after create.
            var maxAgeSeconds = settings.Value.JwtAccessExpires;
            http.Response.Cookies.Append("auth_token", access, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromSeconds(maxAgeSeconds),
                Path = "/",
            });

            var responseRecord = new Record
            {
                ResourceType = Dmart.Models.Enums.ResourceType.User,
                Shortname = user.Shortname,
                Subpath = "/users",
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
        })
        .Accepts<UserCreateBody>("application/json")
        .Produces<Response>()
        // Self-registration is an unauthenticated, OTP-/email-spending,
        // account-minting endpoint — throttle per IP like the other auth routes.
        .RequireRateLimiting("auth-by-ip");
    }
}
