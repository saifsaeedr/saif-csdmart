using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.User;

public static class ProfileHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // GET /user/profile — Python returns records: [Record] with user
        // attributes. The tsdmart SDK reads data.records[0].attributes.
        g.MapGet("/profile", async (HttpContext http, UserService svc,
            DataAdapters.Sql.AccessRepository access, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);
            var user = await svc.GetByShortnameAsync(actor, ct);
            if (user is null)
                return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user missing", ErrorTypes.Db);

            // Python: attributes["permissions"] = await db.get_user_permissions(shortname)
            // Resolves user → roles → permissions into a dict keyed by
            // "space:subpath:resource_type" with allowed_actions, conditions, etc.
            var permissions = await access.GenerateUserPermissionsAsync(actor, ct);

            // Python parity (dmart/api/user/router.py:563-587): optional fields
            // are added only when truthy. StripNulls drops null + "" — matches
            // Python's `if user.X:` guard and response_model_exclude_none.
            var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["email"] = user.Email,
                ["msisdn"] = user.Msisdn,
                ["displayname"] = user.Displayname,
                ["description"] = user.Description,
                ["language"] = user.Language,
                ["type"] = user.Type.ToString().ToLowerInvariant(),
                ["roles"] = user.Roles,
                ["groups"] = user.Groups,
                ["is_email_verified"] = user.IsEmailVerified,
                ["is_msisdn_verified"] = user.IsMsisdnVerified,
                ["force_password_change"] = user.ForcePasswordChange,
                ["payload"] = user.Payload,
                ["permissions"] = permissions,
            };
            var profileRecord = new Record
            {
                ResourceType = Dmart.Models.Enums.ResourceType.User,
                Shortname = user.Shortname,
                Subpath = "users",
                Attributes = AttrHelper.StripNulls(attrs),
            };
            return Response.Ok(new[] { profileRecord });
        });

        g.MapPost("/profile", async (HttpRequest req, HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);

            // Python parity: set_user_profile(profile: core.Record, ...) — the
            // POST body is a Record envelope where every field the handler
            // cares about (password, old_password, email, displayname, ...)
            // lives inside record.attributes. Parse the body once as a raw
            // JSON document, then promote record.attributes to `patch` when
            // the envelope shape is present; otherwise treat the whole doc
            // as the patch (keeps legacy flat-body callers working).
            Dictionary<string, object>? patch;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Response.Fail(InternalErrorCode.INVALID_DATA, "body must be a JSON object", ErrorTypes.Request);

                if (root.TryGetProperty("attributes", out var attrsEl)
                    && attrsEl.ValueKind == JsonValueKind.Object)
                {
                    patch = JsonSerializer.Deserialize(
                        attrsEl.GetRawText(), DmartJsonContext.Default.DictionaryStringObject);
                }
                else
                {
                    patch = JsonSerializer.Deserialize(
                        root.GetRawText(), DmartJsonContext.Default.DictionaryStringObject);
                }
            }
            catch (JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request);
            }

            if (patch is null)
                return Response.Fail(InternalErrorCode.INVALID_DATA, "missing body", ErrorTypes.Request);

            // Python threads `auth_token` into set_user_profile so a
            // firebase_token update lands on the caller's session row only.
            // Pull from Authorization bearer first, fall back to cookie.
            var sessionToken = TryExtractSessionToken(http);
            var result = await svc.UpdateProfileAsync(actor, patch, sessionToken, ct);
            return result.IsOk
                ? Response.Ok(attributes: new() { ["shortname"] = result.Value!.Shortname })
                : Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "request");
        });

        g.MapPost("/delete", async (HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);
            await svc.DeleteAsync(actor, ct);
            return Response.Ok();
        });

        // POST /user/reset — Python parity: mints a fresh invitation for the
        // target user (so they can log in without their old password) AND
        // clears failed-login attempts. The invitation JWTs are returned in
        // the response body since the C# port has no SMTP/SMS delivery.
        // Flips ForcePasswordChange=true so the target must set a new
        // password on first /profile update after invitation login.
        g.MapPost("/reset", async (HttpRequest req, HttpContext http,
            UserRepository users, InvitationService invitationService,
            CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);

            Dictionary<string, object>? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            }
            catch (JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request);
            }
            var target = body?.TryGetValue("shortname", out var sn) == true ? sn?.ToString() : null;
            if (string.IsNullOrEmpty(target))
                return Response.Fail(InternalErrorCode.MISSING_DATA, "shortname required", ErrorTypes.Request);

            var existing = await users.GetByShortnameAsync(target, ct);
            if (existing is null)
                return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user not found", ErrorTypes.Request);

            await users.ResetAttemptsAsync(target, ct);
            // Flip the flag so the next login forces a fresh password choice.
            await users.UpsertAsync(existing with
            {
                ForcePasswordChange = true,
                UpdatedAt = DateTime.UtcNow,
            }, ct);

            var minted = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(existing.Email))
            {
                var t = await invitationService.MintAsync(existing, Dmart.Models.Enums.InvitationChannel.Email, ct);
                if (t is not null) minted["email"] = t;
            }
            if (!string.IsNullOrWhiteSpace(existing.Msisdn))
            {
                var t = await invitationService.MintAsync(existing, Dmart.Models.Enums.InvitationChannel.Sms, ct);
                if (t is not null) minted["msisdn"] = t;
            }

            var attrs = new Dictionary<string, object> { ["shortname"] = target };
            if (minted.Count > 0) attrs["invitations"] = minted;
            return Response.Ok(attributes: attrs);
        });

        // POST /user/validate_password — Python verifies against stored hash,
        // requires authentication. Returns {valid: bool}.
        g.MapPost("/validate_password", async (HttpRequest req, HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);

            Dictionary<string, object>? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            }
            catch (JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request);
            }
            var password = body?.TryGetValue("password", out var pw) == true ? pw?.ToString() : null;
            if (string.IsNullOrEmpty(password))
                return Response.Fail(InternalErrorCode.MISSING_DATA, "password required", ErrorTypes.Request);

            var valid = await svc.ValidatePasswordAsync(actor, password, ct);
            return Response.Ok(attributes: new() { ["valid"] = valid });
        });

        // GET /user/check-existing — Python parity: short-circuit on first
        // conflict. Iteration order matches Python dict: shortname → msisdn →
        // email. Returns {"unique": true} when all free, else
        // {"unique": false, "field": "<name>"}.
        g.MapGet("/check-existing", async (
            string? shortname, string? email, string? msisdn,
            UserRepository users, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(shortname)
                && await users.GetByShortnameAsync(shortname, ct) is not null)
            {
                return Response.Ok(attributes: new()
                {
                    ["unique"] = false,
                    ["field"] = "shortname",
                });
            }
            if (!string.IsNullOrEmpty(msisdn)
                && await users.GetByMsisdnAsync(msisdn, ct) is not null)
            {
                return Response.Ok(attributes: new()
                {
                    ["unique"] = false,
                    ["field"] = "msisdn",
                });
            }
            if (!string.IsNullOrEmpty(email)
                && await users.GetByEmailAsync(email, ct) is not null)
            {
                return Response.Ok(attributes: new()
                {
                    ["unique"] = false,
                    ["field"] = "email",
                });
            }

            return Response.Ok(attributes: new() { ["unique"] = true });
        });
    }

    // Extract the caller's access token so UserService can update the exact
    // session row they're authenticated under (Python parity — `auth_token`
    // threaded through set_user_profile). Authorization header wins; fall
    // back to the auth_token cookie issued by /user/login. Returns null when
    // neither source is present (e.g. during anonymous access).
    private static string? TryExtractSessionToken(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
        {
            const string bearer = "Bearer ";
            if (auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                return auth.Substring(bearer.Length).Trim();
        }
        var cookie = http.Request.Cookies["auth_token"];
        return string.IsNullOrEmpty(cookie) ? null : cookie;
    }
}
