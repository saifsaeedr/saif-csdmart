using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dmart.Auth;

public static class JwtBearerSetup
{
    public static IServiceCollection AddDmartAuth(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Bind JwtBearerOptions LAZILY against IOptions<DmartSettings> so the secret
        // resolves AFTER any test/in-memory config sources have been merged.
        // Reading cfg directly here would bake in the config.env value before
        // WebApplicationFactory adds its test overrides.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<DmartSettings>>((bearer, dmartOpts) =>
            {
                var s = dmartOpts.Value;
                // DmartSettingsValidator rejects JwtSecret shorter than 32 bytes at
                // startup via ValidateOnStart, so by the time this lazy binding runs
                // we're guaranteed a sufficiently-long secret.
                var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(s.JwtSecret));

                // Only require HTTPS in production; dev/test use HTTP.
                var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                bearer.RequireHttpsMetadata = env != "Development" && env != "Testing";
                bearer.IncludeErrorDetails = env == "Development";
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = s.JwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = s.JwtAudience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    IssuerSigningKeys = new[] { signingKey },
                    // .NET 9+ JsonWebTokenHandler looks keys up by kid; our hand-rolled
                    // JWT has no kid, so we always return the symmetric key directly.
                    IssuerSigningKeyResolver = (_, _, _, _) => new[] { signingKey },
                    NameClaimType = "sub",
                    // Default ClockSkew is 5 minutes — tokens stay valid for
                    // 5m past `exp`. That made JWT_ACCESS_EXPIRES=1 appear to
                    // be ignored (1-sec tokens stayed live for 5 min). Python
                    // dmart has no skew tolerance, so match: 0.
                    ClockSkew = TimeSpan.Zero,
                };

                // dmart Python accepts the JWT from EITHER the Authorization header
                // OR the auth_token cookie. Browser clients depend on the cookie.
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (string.IsNullOrEmpty(ctx.Token))
                        {
                            var fromCookie = ctx.Request.Cookies["auth_token"];
                            // CSRF: only let the cookie act as a credential when
                            // the request isn't cross-site (see CookieAuthAllowed).
                            if (!string.IsNullOrEmpty(fromCookie) && CookieAuthAllowed(ctx.HttpContext))
                                ctx.Token = fromCookie;
                        }
                        return Task.CompletedTask;
                    },
                    // After a token's signature + lifetime have passed, also
                    // enforce DB-backed session state. Logout/password changes/
                    // account deactivation delete session rows, so every
                    // non-bot request must still have a live row even when
                    // inactivity expiry is disabled.
                    OnTokenValidated = async ctx =>
                    {
                        var settings = ctx.HttpContext.RequestServices
                            .GetRequiredService<IOptions<DmartSettings>>().Value;
                        // .NET 9's JsonWebTokenHandler exposes the raw token string
                        // on JsonWebToken.EncodedToken; older code paths use
                        // JwtSecurityToken.RawData. Try both.
                        var jwt = ctx.SecurityToken as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;
                        var raw = jwt?.EncodedToken
                            ?? (ctx.SecurityToken as System.IdentityModel.Tokens.Jwt.JwtSecurityToken)?.RawData;
                        if (string.IsNullOrEmpty(raw))
                        {
                            ctx.Fail(new SecurityTokenException("missing raw token"));
                            return;
                        }

                        // A refresh token must never act as an access token.
                        // Session binding incidentally blocks this for web
                        // users (the sessions row stores the access token),
                        // but bot users skip the session check below — so
                        // without this guard a leaked bot refresh token works
                        // on every API endpoint. Tokens WITHOUT the claim
                        // predate the 2026-06 hardening (the EOL Python
                        // implementation never wrote token_use): they pass by
                        // default — recorded so operators can tell when the
                        // installed base has aged out — and are rejected once
                        // JwtRequireTokenUse is set. Mirrors the
                        // access-as-refresh guard in OAuthEndpoints.
                        var tokenUse = ReadTokenUse(jwt, raw);
                        if (tokenUse is null)
                        {
                            if (settings.JwtRequireTokenUse)
                            {
                                ctx.Fail(new SecurityTokenException(
                                    "token missing token_use claim"));
                                return;
                            }
                            ctx.HttpContext.RequestServices
                                .GetRequiredService<LegacyTokenMonitor>()
                                .Record(ctx.Principal?.Identity?.Name, "bearer");
                        }
                        else if (settings.JwtRequireTokenUse
                            ? !string.Equals(tokenUse, TokenUse.Access, StringComparison.Ordinal)
                            : string.Equals(tokenUse, TokenUse.Refresh, StringComparison.Ordinal))
                        {
                            ctx.Fail(new SecurityTokenException(
                                "refresh token presented as access token"));
                            return;
                        }

                        var users = ctx.HttpContext.RequestServices
                            .GetRequiredService<DataAdapters.Sql.UserRepository>();
                        var actor = ctx.Principal?.Identity?.Name;
                        if (string.IsNullOrEmpty(actor))
                        {
                            ctx.Fail(new SecurityTokenException("missing subject"));
                            return;
                        }

                        var user = await users.GetByShortnameAsync(actor, ctx.HttpContext.RequestAborted);
                        if (user is null || !user.IsActive)
                        {
                            ctx.Fail(new SecurityTokenException("user is inactive"));
                            return;
                        }

                        // Python parity: bot users skip session-row creation and
                        // session-inactivity checks, but the user row must still
                        // exist and be active.
                        if (user.Type == UserType.Bot) return;

                        var liveSession = settings.SessionInactivityTtl > 0
                            ? await users.TouchSessionAsync(
                                actor, raw, settings.SessionInactivityTtl, ctx.HttpContext.RequestAborted)
                            : await users.IsSessionValidAsync(actor, raw, ctx.HttpContext.RequestAborted);
                        if (!liveSession)
                            ctx.Fail(new SecurityTokenException("session expired or revoked"));
                    },
                    // Return a JSON error body matching Python's api.Response shape:
                    // {"status":"failed","error":{"type":"jwtauth","code":N,"message":"..."}}
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse(); // suppress default empty-body 401

                        // Python parity — messages come from
                        // dmart_plain/backend/utils/jwt.py so clients keying
                        // off error.message (e.g. for i18n) see the same
                        // strings regardless of runtime.
                        int code = InternalErrorCode.NOT_AUTHENTICATED;
                        string message = "Not authenticated [1]";

                        if (ctx.AuthenticateFailure is SecurityTokenExpiredException)
                        {
                            code = InternalErrorCode.EXPIRED_TOKEN;
                            message = "Expired Token";
                        }
                        else if (ctx.AuthenticateFailure is SecurityTokenException)
                        {
                            code = InternalErrorCode.INVALID_TOKEN;
                            message = "Invalid Token [1]";
                        }

                        // MCP clients (Zed, Cursor, Claude Desktop) only kick
                        // off OAuth discovery *after* they see a 401 with a
                        // `WWW-Authenticate: Bearer resource_metadata=...`
                        // header pointing at the protected-resource document.
                        // Emit it for any 401 on an /mcp* path. The metadata
                        // URL is on the same origin as the request, so clients
                        // behind any reverse-proxy path base still find it.
                        if (ctx.Request.Path.StartsWithSegments("/mcp"))
                        {
                            var dmartSettings = ctx.HttpContext.RequestServices
                                .GetRequiredService<IOptions<DmartSettings>>().Value;
                            var base_ = Uri.TryCreate(dmartSettings.JwtIssuer, UriKind.Absolute, out var u)
                                && (u.Scheme == "https" || u.Scheme == "http")
                                ? dmartSettings.JwtIssuer!.TrimEnd('/')
                                : $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}".TrimEnd('/');
                            var prmUrl = $"{base_}/.well-known/oauth-protected-resource";
                            ctx.Response.Headers["WWW-Authenticate"] =
                                $"Bearer realm=\"dmart-mcp\", resource_metadata=\"{prmUrl}\"";
                        }

                        ctx.Response.StatusCode = 401;
                        ctx.Response.ContentType = "application/json";
                        var body = Response.Fail(code, message, ErrorTypes.JwtAuth);
                        await ctx.Response.WriteAsync(
                            JsonSerializer.Serialize(body, DmartJsonContext.Default.Response));
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }

    // Read the `token_use` claim. Prefer the parsed JsonWebToken's payload;
    // fall back to decoding the raw token (covers the JwtSecurityToken path).
    // The signature is already verified by the time this runs. Returns null
    // when the claim is absent (pre-2026-06-hardening tokens).
    private static string? ReadTokenUse(
        Microsoft.IdentityModel.JsonWebTokens.JsonWebToken? jwt, string raw)
    {
        if (jwt is not null && jwt.TryGetPayloadValue<string>("token_use", out var tu))
            return tu;
        return TokenUse.Read(raw);
    }

    // CSRF defense for cookie-borne auth. The auth_token cookie is promoted to a
    // bearer credential only when the request is NOT cross-site. Modern browsers
    // send Sec-Fetch-Site automatically (same-origin/same-site/none for
    // first-party use; cross-site for a forged request), and an explicit
    // X-Requested-With is accepted for legacy clients — a cross-site form POST
    // or top-level navigation can forge neither, so it can't ride a victim's
    // cookie. Bearer-header (Authorization) callers never reach this path.
    private static bool CookieAuthAllowed(Microsoft.AspNetCore.Http.HttpContext http)
    {
        var settings = http.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
        if (!settings.CsrfProtectCookieAuth) return true;

        var fetchSite = http.Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite is "same-origin" or "same-site" or "none") return true;
        if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase)) return false;
        // No Fetch-Metadata (older browser / non-browser client): fall back to
        // requiring the custom header, which a cross-site form cannot set.
        return http.Request.Headers.ContainsKey("X-Requested-With");
    }
}
