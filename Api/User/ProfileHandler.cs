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
            var actor = http.User.Identity?.Name;
            if (actor is null) return Response.Fail("unauthorized", "login required");
            var user = await svc.GetByShortnameAsync(actor, ct);
            if (user is null) return Response.Fail("not_found", "user missing");

            // Python: attributes["permissions"] = await db.get_user_permissions(shortname)
            // Resolves user → roles → permissions into a dict keyed by
            // "space:subpath:resource_type" with allowed_actions, conditions, etc.
            var permissions = await access.GenerateUserPermissionsAsync(actor, ct);

            var profileRecord = new Record
            {
                ResourceType = Dmart.Models.Enums.ResourceType.User,
                Shortname = user.Shortname,
                Subpath = "users",
                Attributes = new()
                {
                    ["email"] = user.Email ?? (object)"",
                    ["msisdn"] = user.Msisdn ?? (object)"",
                    ["displayname"] = user.Displayname ?? (object)"",
                    ["description"] = user.Description ?? (object)"",
                    ["language"] = user.Language,
                    ["type"] = user.Type.ToString().ToLowerInvariant(),
                    ["roles"] = user.Roles,
                    ["groups"] = user.Groups,
                    ["is_email_verified"] = user.IsEmailVerified,
                    ["is_msisdn_verified"] = user.IsMsisdnVerified,
                    ["force_password_change"] = user.ForcePasswordChange,
                    ["payload"] = user.Payload ?? (object)new Dictionary<string, object>(),
                    ["permissions"] = permissions,
                },
            };
            return Response.Ok(new[] { profileRecord });
        });

        g.MapPost("/profile", async (HttpRequest req, HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name;
            if (actor is null) return Response.Fail("unauthorized", "login required");
            var patch = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            if (patch is null) return Response.Fail("bad_request", "missing body");
            var result = await svc.UpdateProfileAsync(actor, patch, ct);
            return result.IsOk
                ? Response.Ok(attributes: new() { ["shortname"] = result.Value!.Shortname })
                : Response.Fail(result.ErrorCode!, result.ErrorMessage!);
        });

        g.MapPost("/delete", async (HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name;
            if (actor is null) return Response.Fail("unauthorized", "login required");
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
            var actor = http.User.Identity?.Name;
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", "auth");

            Dictionary<string, object>? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            }
            catch (JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, "request");
            }
            var target = body?.TryGetValue("shortname", out var sn) == true ? sn?.ToString() : null;
            if (string.IsNullOrEmpty(target))
                return Response.Fail(InternalErrorCode.MISSING_DATA, "shortname required", "request");

            var existing = await users.GetByShortnameAsync(target, ct);
            if (existing is null)
                return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user not found", "request");

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
            var actor = http.User.Identity?.Name;
            if (actor is null)
                return Response.Fail("unauthorized", "login required");

            Dictionary<string, object>? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            }
            catch (JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, "request");
            }
            var password = body?.TryGetValue("password", out var pw) == true ? pw?.ToString() : null;
            if (string.IsNullOrEmpty(password))
                return Response.Fail("bad_request", "password required");

            var valid = await svc.ValidatePasswordAsync(actor, password, ct);
            return Response.Ok(attributes: new() { ["valid"] = valid });
        });

        // GET /user/check-existing — Python returns per-field {shortname, email, msisdn} booleans.
        g.MapGet("/check-existing", async (
            string? shortname, string? email, string? msisdn,
            UserRepository users, CancellationToken ct) =>
        {
            var snExists = !string.IsNullOrEmpty(shortname)
                && await users.GetByShortnameAsync(shortname, ct) is not null;
            var emailExists = !string.IsNullOrEmpty(email)
                && await users.GetByEmailAsync(email, ct) is not null;
            var msisdnExists = !string.IsNullOrEmpty(msisdn)
                && await users.GetByMsisdnAsync(msisdn, ct) is not null;

            return Response.Ok(attributes: new()
            {
                ["shortname"] = snExists,
                ["email"] = emailExists,
                ["msisdn"] = msisdnExists,
            });
        });
    }
}
