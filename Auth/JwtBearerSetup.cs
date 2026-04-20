using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Api;
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
                if (s.JwtSecret.Length < 32)
                {
                    var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole()).CreateLogger("JwtBearerSetup");
                    logger.LogWarning("JWT secret is shorter than 32 bytes — this is insecure for HS256");
                }
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
                            if (!string.IsNullOrEmpty(fromCookie))
                                ctx.Token = fromCookie;
                        }
                        return Task.CompletedTask;
                    },
                    // After a token's signature + lifetime have passed, also
                    // enforce session inactivity if configured. If the session
                    // is older than settings.SessionInactivityTtl, the row is
                    // evicted and authentication fails.
                    OnTokenValidated = async ctx =>
                    {
                        var settings = ctx.HttpContext.RequestServices
                            .GetRequiredService<IOptions<DmartSettings>>().Value;
                        if (settings.SessionInactivityTtl <= 0) return;
                        // .NET 9's JsonWebTokenHandler exposes the raw token string
                        // on JsonWebToken.EncodedToken; older code paths use
                        // JwtSecurityToken.RawData. Try both.
                        var raw = ctx.SecurityToken is Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jwt
                            ? jwt.EncodedToken
                            : (ctx.SecurityToken as System.IdentityModel.Tokens.Jwt.JwtSecurityToken)?.RawData;
                        if (string.IsNullOrEmpty(raw)) return;
                        var users = ctx.HttpContext.RequestServices
                            .GetRequiredService<DataAdapters.Sql.UserRepository>();
                        var touched = await users.TouchSessionAsync(
                            raw, settings.SessionInactivityTtl, ctx.HttpContext.RequestAborted);
                        if (!touched)
                            ctx.Fail(new SecurityTokenException("session expired due to inactivity"));
                    },
                    // Return a JSON error body matching Python's api.Response shape:
                    // {"status":"failed","error":{"type":"jwtauth","code":N,"message":"..."}}
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse(); // suppress default empty-body 401

                        int code = InternalErrorCode.NOT_AUTHENTICATED;
                        string message = "Not authenticated";

                        if (ctx.AuthenticateFailure is SecurityTokenExpiredException)
                        {
                            code = InternalErrorCode.EXPIRED_TOKEN;
                            message = "Token has expired";
                        }
                        else if (ctx.AuthenticateFailure is SecurityTokenException)
                        {
                            code = InternalErrorCode.INVALID_TOKEN;
                            message = "Invalid token";
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
}
