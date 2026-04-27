using System.Net;
using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Oauth;

// OAuth 2.1 Authorization Server + Dynamic Client Registration surface for
// Model Context Protocol clients. Implements the MCP authorization profile:
//   - RFC 8414 authorization server metadata + MCP's protected-resource extension
//   - RFC 7591 dynamic client registration (public clients, no client_secret)
//   - RFC 6749 authorization code grant with RFC 7636 PKCE (S256 only
//     enforced; `plain` is accepted by the spec but rejected here)
//
// The issued access token is a dmart JWT (via JwtIssuer) so the rest of the
// pipeline (JwtBearer middleware, PermissionService, etc.) authenticates the
// caller exactly the same way as password login. No special "MCP session"
// token type — one token format, one validator.
//
// The authorize endpoint serves a tiny HTML login form. That's deliberate:
// bundling the form in-server keeps MCP onboarding a single-binary deploy, no
// frontend coupling. We render plain HTML with inline styles — zero assets.
public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapOAuth(this IEndpointRouteBuilder app)
    {
        // ---- Discovery ----

        // MCP protected-resource metadata (MCP 2025-03-26 spec extension).
        // Tells MCP clients where to find the authorization server. Served at
        // the *same* origin the client tried (so Claude Desktop fetching
        // http://host:port/.well-known/oauth-protected-resource gets this).
        app.MapGet("/.well-known/oauth-protected-resource",
            (HttpContext http, IOptions<DmartSettings> settings) =>
                ProtectedResourceMetadata(http, settings.Value))
            .WithTags("OAuth");

        // RFC 8414 authorization-server metadata. Same origin.
        app.MapGet("/.well-known/oauth-authorization-server",
            (HttpContext http, IOptions<DmartSettings> settings) =>
                AuthorizationServerMetadata(http, settings.Value))
            .WithTags("OAuth");

        var g = app.MapGroup("/oauth").WithTags("OAuth");

        // ---- Dynamic Client Registration (RFC 7591) ----
        //
        // Public clients only. The client posts redirect_uris + client_name;
        // we mint a client_id, echo the registration back. No client_secret
        // is issued — public clients prove themselves at token time via PKCE.
        g.MapPost("/register", HandleRegisterAsync);

        // ---- Authorization endpoint (RFC 6749 §3.1) ----
        //
        // GET  — render a login form. The user enters shortname+password;
        //        we verify, mint an auth code bound to (client_id,
        //        redirect_uri, code_challenge), and 302 to
        //        `redirect_uri?code=<code>&state=<state>`.
        //
        // POST — same form submits back to this endpoint; we validate
        //        credentials and return the same 302 on success.
        g.MapGet("/authorize", HandleAuthorizeGet);
        g.MapPost("/authorize", HandleAuthorizePostAsync).RequireRateLimiting("auth-by-ip");

        // ---- Token endpoint (RFC 6749 §3.2) ----
        //
        // Exchanges the auth code + PKCE verifier for a dmart access token.
        // Issues a matching refresh token (standard dmart JWT).
        g.MapPost("/token", HandleTokenAsync).RequireRateLimiting("auth-by-ip");

        return app;
    }

    // ---- Metadata shapes ----

    private static IResult ProtectedResourceMetadata(HttpContext http, DmartSettings settings)
    {
        var issuer = GetIssuerUrl(http, settings);
        // The `resource` field identifies the MCP endpoint itself per RFC 9728 /
        // MCP's protected-resource profile. Pointing it at the bare origin would
        // be ambiguous — clients need the exact URL of the protected resource.
        var resource = $"{issuer}/mcp";
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("resource", resource);
            w.WriteString("resource_name", "dmart");
            w.WriteStartArray("authorization_servers");
            w.WriteStringValue(issuer);
            w.WriteEndArray();
            w.WriteStartArray("bearer_methods_supported");
            w.WriteStringValue("header");
            w.WriteEndArray();
            w.WriteStartArray("scopes_supported");
            w.WriteStringValue("mcp");
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Results.Bytes(ms.ToArray(), "application/json");
    }

    private static IResult AuthorizationServerMetadata(HttpContext http, DmartSettings settings)
    {
        var issuer = GetIssuerUrl(http, settings);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("issuer", issuer);
            w.WriteString("authorization_endpoint", $"{issuer}/oauth/authorize");
            w.WriteString("token_endpoint", $"{issuer}/oauth/token");
            w.WriteString("registration_endpoint", $"{issuer}/oauth/register");

            w.WriteStartArray("response_types_supported");
            w.WriteStringValue("code");
            w.WriteEndArray();

            w.WriteStartArray("grant_types_supported");
            w.WriteStringValue("authorization_code");
            w.WriteStringValue("refresh_token");
            w.WriteEndArray();

            w.WriteStartArray("token_endpoint_auth_methods_supported");
            w.WriteStringValue("none");
            w.WriteEndArray();

            w.WriteStartArray("code_challenge_methods_supported");
            w.WriteStringValue("S256");
            w.WriteEndArray();

            w.WriteStartArray("scopes_supported");
            w.WriteStringValue("mcp");
            w.WriteEndArray();

            w.WriteBoolean("require_pushed_authorization_requests", false);
            w.WriteEndObject();
        }
        return Results.Bytes(ms.ToArray(), "application/json");
    }

    // ---- /oauth/register ----

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext http, OAuthClientStore clients, CancellationToken ct)
    {
        RegisterRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(
                http.Request.Body, DmartJsonContext.Default.RegisterRequest, ct);
        }
        catch (JsonException)
        {
            return JsonError(400, "invalid_client_metadata", "malformed registration body");
        }

        if (body is null || body.RedirectUris is null || body.RedirectUris.Count == 0)
            return JsonError(400, "invalid_redirect_uri", "redirect_uris is required");

        OAuthClientStore.Client client;
        try
        {
            client = clients.Register(body.RedirectUris, body.ClientName ?? "mcp-client");
        }
        catch (ArgumentException ex)
        {
            return JsonError(400, "invalid_redirect_uri", ex.Message);
        }

        var resp = new RegisterResponse(
            ClientId: client.ClientId,
            ClientName: client.ClientName,
            RedirectUris: client.RedirectUris.ToList(),
            TokenEndpointAuthMethod: "none",
            GrantTypes: new List<string> { "authorization_code", "refresh_token" },
            ResponseTypes: new List<string> { "code" });
        return Results.Json(resp, DmartJsonContext.Default.RegisterResponse, statusCode: 201);
    }

    // ---- /oauth/authorize (GET) — render login form ----

    private static IResult HandleAuthorizeGet(HttpRequest req, OAuthClientStore clients)
    {
        var p = ReadAuthorizeParams(req.Query);
        var validation = ValidateAuthorizeParams(p, clients);
        if (validation is not null) return validation;
        return HtmlLoginForm(p, error: null);
    }

    // ---- /oauth/authorize (POST) — authenticate + issue code ----

    private static async Task<IResult> HandleAuthorizePostAsync(HttpContext http,
        OAuthClientStore clients, OAuthCodeStore codes, UserService users,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var p = ReadAuthorizeParams(form);
        var validation = ValidateAuthorizeParams(p, clients);
        if (validation is not null) return validation;

        var shortname = form["shortname"].ToString();
        var password = form["password"].ToString();
        if (string.IsNullOrEmpty(shortname) || string.IsNullOrEmpty(password))
            return HtmlLoginForm(p, error: "enter your shortname and password");

        var loginReq = new UserLoginRequest(
            Shortname: shortname, Email: null, Msisdn: null,
            Password: password, Invitation: null);
        var result = await users.LoginAsync(loginReq, requestHeaders: null, ct);
        if (!result.IsOk)
            return HtmlLoginForm(p, error: "invalid credentials");

        // Issue the authorization code bound to the user + client context.
        var code = codes.Issue(
            userShortname: result.Value.User.Shortname,
            clientId: p.ClientId!,
            redirectUri: p.RedirectUri!,
            codeChallenge: p.CodeChallenge,
            codeChallengeMethod: p.CodeChallengeMethod,
            scope: p.Scope ?? "mcp");

        // Redirect back to the client. The state is echoed verbatim — MCP
        // clients use it as the CSRF token.
        var redirect = BuildCallbackUrl(p.RedirectUri!, code, p.State);
        return Results.Redirect(redirect);
    }

    // ---- /oauth/token ----

    private static async Task<IResult> HandleTokenAsync(HttpContext http,
        OAuthCodeStore codes, JwtIssuer jwt, UserService users,
        UserRepository userRepo,
        IOptions<DmartSettings> settings, CancellationToken ct)
    {
        // application/x-www-form-urlencoded per RFC 6749.
        var form = await http.Request.ReadFormAsync(ct);
        var grantType = form["grant_type"].ToString();

        if (grantType == "authorization_code")
            return await ExchangeCodeAsync(form, codes, jwt, users, userRepo, settings.Value, ct);
        if (grantType == "refresh_token")
            return await RefreshAsync(form, jwt, users, userRepo, settings.Value, ct);

        return OAuthError(400, "unsupported_grant_type",
            $"grant_type `{grantType}` not supported");
    }

    private static async Task<IResult> ExchangeCodeAsync(IFormCollection form,
        OAuthCodeStore codes, JwtIssuer jwt, UserService users,
        UserRepository userRepo,
        DmartSettings settings, CancellationToken ct)
    {
        var code = form["code"].ToString();
        var clientId = form["client_id"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var verifier = form["code_verifier"].ToString();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(clientId)
            || string.IsNullOrEmpty(redirectUri))
            return OAuthError(400, "invalid_request",
                "code, client_id, and redirect_uri are required");

        var entry = codes.Consume(code, redirectUri, clientId, verifier);
        if (entry is null)
            return OAuthError(400, "invalid_grant", "code is invalid, expired, or PKCE failed");

        var user = await users.GetByShortnameAsync(entry.UserShortname, ct);
        if (user is null)
            return OAuthError(400, "invalid_grant", "user no longer exists");

        var access = jwt.IssueAccess(user.Shortname, user.Roles, user.Type);
        var refresh = jwt.IssueRefresh(user.Shortname, user.Type);

        // The new strict JwtBearerSetup.OnTokenValidated requires a matching
        // sessions row for non-bot tokens. Mirror ProcessLoginAsync (Services/
        // UserService.cs:541) so OAuth-issued access tokens authenticate.
        if (user.Type != UserType.Bot)
            await userRepo.CreateSessionAsync(user.Shortname, access, null, ct);

        return TokenResponse(access, refresh, settings, entry.Scope);
    }

    private static async Task<IResult> RefreshAsync(IFormCollection form,
        JwtIssuer jwt, UserService users, UserRepository userRepo,
        DmartSettings settings, CancellationToken ct)
    {
        var refreshToken = form["refresh_token"].ToString();
        if (string.IsNullOrEmpty(refreshToken))
            return OAuthError(400, "invalid_request", "refresh_token is required");

        var principal = jwt.Validate(refreshToken);
        if (principal?.Identity?.Name is not string shortname)
            return OAuthError(400, "invalid_grant", "invalid refresh token");

        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is null)
            return OAuthError(400, "invalid_grant", "user no longer exists");
        // Re-check IsActive on every refresh. Without this, deactivating a
        // compromised account doesn't cut off its refresh tokens — they keep
        // minting fresh access tokens until the refresh JWT's own expiry.
        if (!user.IsActive)
            return OAuthError(400, "invalid_grant", "user is no longer active");

        // Enforce absolute session lifetime. The incoming refresh carries the
        // original login's iat (preserved across rotations below), so once
        // SessionMaxLifetimeSeconds elapses we reject — the user must log in
        // again. This caps a stolen refresh's usefulness regardless of how
        // aggressively the attacker rotates it.
        var originalIat = ExtractIatUnix(refreshToken);
        if (settings.SessionMaxLifetimeSeconds > 0 && originalIat is long iatUnix)
        {
            var ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - iatUnix;
            if (ageSeconds > settings.SessionMaxLifetimeSeconds)
                return OAuthError(400, "invalid_grant", "session exceeded maximum lifetime");
        }

        var access = jwt.IssueAccess(user.Shortname, user.Roles, user.Type);
        var newRefresh = jwt.IssueRefresh(user.Shortname, user.Type, originalIat);

        if (user.Type != UserType.Bot)
            await userRepo.CreateSessionAsync(user.Shortname, access, null, ct);

        return TokenResponse(access, newRefresh, settings, scope: "mcp");
    }

    // Parse the `iat` claim out of a signed JWT. Returns null when the token
    // is malformed or missing the claim — the caller already verified the
    // signature via jwt.Validate, so this is purely about reading a known-good
    // payload.
    private static long? ExtractIatUnix(string jwtToken)
    {
        var parts = jwtToken.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var payload = Convert.FromBase64String(padded);
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("iat", out var iat) ? iat.GetInt64() : null;
        }
        catch { return null; }
    }

    private static IResult TokenResponse(string access, string refresh,
        DmartSettings settings, string scope)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("access_token", access);
            w.WriteString("token_type", "Bearer");
            w.WriteNumber("expires_in", settings.JwtAccessExpires);
            w.WriteString("refresh_token", refresh);
            w.WriteString("scope", scope);
            w.WriteEndObject();
        }
        return Results.Bytes(ms.ToArray(), "application/json");
    }

    // ---- Authorize param helpers ----

    private sealed record AuthorizeParams(
        string? ResponseType,
        string? ClientId,
        string? RedirectUri,
        string? Scope,
        string? State,
        string? CodeChallenge,
        string? CodeChallengeMethod);

    private static AuthorizeParams ReadAuthorizeParams(IQueryCollection q) => new(
        ResponseType: q["response_type"].ToString(),
        ClientId: q["client_id"].ToString(),
        RedirectUri: q["redirect_uri"].ToString(),
        Scope: q["scope"].ToString(),
        State: q["state"].ToString(),
        CodeChallenge: q["code_challenge"].ToString(),
        CodeChallengeMethod: q["code_challenge_method"].ToString());

    private static AuthorizeParams ReadAuthorizeParams(IFormCollection f) => new(
        ResponseType: f["response_type"].ToString(),
        ClientId: f["client_id"].ToString(),
        RedirectUri: f["redirect_uri"].ToString(),
        Scope: f["scope"].ToString(),
        State: f["state"].ToString(),
        CodeChallenge: f["code_challenge"].ToString(),
        CodeChallengeMethod: f["code_challenge_method"].ToString());

    // Returns IResult (error page) if params are invalid, null if OK. Follows
    // the spec: errors visible to the user (bad client_id / redirect_uri) are
    // shown as a plain page; errors the client can recover from (missing
    // response_type, bad challenge) are redirected back with `error=`.
    private static IResult? ValidateAuthorizeParams(AuthorizeParams p, OAuthClientStore clients)
    {
        if (string.IsNullOrEmpty(p.ClientId))
            return HtmlPage(400, "Missing client_id.");
        if (string.IsNullOrEmpty(p.RedirectUri))
            return HtmlPage(400, "Missing redirect_uri.");
        if (!clients.ValidateRedirectUri(p.ClientId, p.RedirectUri))
            return HtmlPage(400, "Unknown client_id or redirect_uri mismatch.");

        // From here on, errors can safely be redirected back to the client.
        if (p.ResponseType != "code")
            return Results.Redirect(BuildErrorUrl(p.RedirectUri, "unsupported_response_type",
                $"response_type must be `code`, got `{p.ResponseType}`", p.State));
        if (string.IsNullOrEmpty(p.CodeChallenge))
            return Results.Redirect(BuildErrorUrl(p.RedirectUri, "invalid_request",
                "code_challenge is required (PKCE)", p.State));
        if (p.CodeChallengeMethod is not "S256" and not null and not "")
            return Results.Redirect(BuildErrorUrl(p.RedirectUri, "invalid_request",
                "only S256 code_challenge_method is supported", p.State));
        return null;
    }

    // ---- URL builders ----

    private static string GetIssuerUrl(HttpContext http, DmartSettings settings)
    {
        // If JwtIssuer is an absolute URL, prefer it — it's what JwtBearer
        // middleware validates against. Otherwise synthesize from the request.
        if (Uri.TryCreate(settings.JwtIssuer, UriKind.Absolute, out var u)
            && (u.Scheme == "https" || u.Scheme == "http"))
        {
            return settings.JwtIssuer!.TrimEnd('/');
        }
        var req = http.Request;
        return $"{req.Scheme}://{req.Host.Value}".TrimEnd('/');
    }

    private static string BuildCallbackUrl(string redirectUri, string code, string? state)
    {
        var sb = new StringBuilder(redirectUri);
        sb.Append(redirectUri.Contains('?') ? '&' : '?');
        sb.Append("code=").Append(WebUtility.UrlEncode(code));
        if (!string.IsNullOrEmpty(state))
            sb.Append("&state=").Append(WebUtility.UrlEncode(state));
        return sb.ToString();
    }

    private static string BuildErrorUrl(string redirectUri, string code,
        string description, string? state)
    {
        var sb = new StringBuilder(redirectUri);
        sb.Append(redirectUri.Contains('?') ? '&' : '?');
        sb.Append("error=").Append(WebUtility.UrlEncode(code));
        sb.Append("&error_description=").Append(WebUtility.UrlEncode(description));
        if (!string.IsNullOrEmpty(state))
            sb.Append("&state=").Append(WebUtility.UrlEncode(state));
        return sb.ToString();
    }

    // ---- HTML rendering ----
    //
    // The login form. Minimal markup; inline styles keep it self-contained.
    // Honors the X-Forwarded-Proto header implicitly because we only render
    // relative form actions.

    private static IResult HtmlLoginForm(AuthorizeParams p, string? error)
    {
        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none';" />
              <title>dmart — authorize MCP client</title>
              <style>
                body { font-family: -apple-system, system-ui, sans-serif; background:#0f172a; color:#e5e7eb;
                       display:flex; align-items:center; justify-content:center; min-height:100vh; margin:0; }
                .card { background:#1e293b; padding:2rem; border-radius:12px; width:360px;
                        box-shadow: 0 10px 40px rgba(0,0,0,.4); }
                h1 { margin:0 0 .5rem 0; font-size:1.25rem; }
                p.sub { margin:0 0 1.5rem 0; color:#94a3b8; font-size:.9rem; }
                label { display:block; font-size:.85rem; margin-bottom:.25rem; color:#cbd5e1; }
                input { width:100%; padding:.6rem .75rem; box-sizing:border-box; margin-bottom:1rem;
                        border:1px solid #334155; background:#0f172a; color:#e5e7eb; border-radius:6px; }
                button { width:100%; padding:.7rem; background:#2563eb; color:white; border:none;
                         border-radius:6px; font-weight:600; cursor:pointer; }
                button:hover { background:#1d4ed8; }
                .err { background:#7f1d1d; padding:.5rem .75rem; border-radius:6px; margin-bottom:1rem;
                       color:#fecaca; font-size:.85rem; }
                .client { font-family: monospace; background:#0f172a; padding:.25rem .4rem;
                          border-radius:4px; color:#94a3b8; font-size:.8rem; }
              </style>
            </head>
            <body><div class="card">
              <h1>Authorize MCP client</h1>
            """);
        html.Append("<p class=\"sub\">Sign in to let <span class=\"client\">");
        html.Append(HtmlEncode(p.ClientId ?? "client"));
        html.Append("</span> access dmart as you.</p>");

        if (!string.IsNullOrEmpty(error))
        {
            html.Append("<div class=\"err\">");
            html.Append(HtmlEncode(error));
            html.Append("</div>");
        }

        // Empty action = post back to the current URL. Browsers resolve the
        // empty string against the page's own URL, which is whatever external
        // path the reverse proxy exposed (e.g. /dmart/oauth/authorize). An
        // absolute "/oauth/authorize" would break when dmart is mounted under
        // a sub-path — the POST would land on the origin root, which doesn't
        // route to dmart.
        html.Append("<form method=\"post\" action=\"\">");
        AppendHidden(html, "response_type", p.ResponseType);
        AppendHidden(html, "client_id", p.ClientId);
        AppendHidden(html, "redirect_uri", p.RedirectUri);
        AppendHidden(html, "scope", p.Scope);
        AppendHidden(html, "state", p.State);
        AppendHidden(html, "code_challenge", p.CodeChallenge);
        AppendHidden(html, "code_challenge_method", p.CodeChallengeMethod);
        html.Append("""
            <label for="shortname">Shortname</label>
            <input id="shortname" name="shortname" autocomplete="username" autofocus required>
            <label for="password">Password</label>
            <input id="password" name="password" type="password" autocomplete="current-password" required>
            <button type="submit">Sign in and authorize</button>
            </form>
            </div></body></html>
            """);
        return Results.Content(html.ToString(), "text/html; charset=utf-8");
    }

    private static IResult HtmlPage(int status, string message)
    {
        var body = $"""
            <!doctype html><meta charset="utf-8">
            <title>OAuth error</title>
            <div style="font-family:system-ui,sans-serif;padding:2rem;max-width:500px;margin:2rem auto;
                        background:#fef2f2;color:#991b1b;border-radius:8px;border:1px solid #fecaca;">
              <h2 style="margin:0 0 .5rem 0;">Authorization failed</h2>
              <p style="margin:0;">{HtmlEncode(message)}</p>
            </div>
            """;
        return Results.Content(body, "text/html; charset=utf-8", statusCode: status);
    }

    private static void AppendHidden(StringBuilder html, string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        html.Append("<input type=\"hidden\" name=\"");
        html.Append(HtmlEncode(name));
        html.Append("\" value=\"");
        html.Append(HtmlEncode(value));
        html.Append("\">");
    }

    private static string HtmlEncode(string s) => WebUtility.HtmlEncode(s);

    // ---- Error JSON ----

    private static IResult OAuthError(int status, string error, string description)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("error", error);
            w.WriteString("error_description", description);
            w.WriteEndObject();
        }
        return Results.Text(Encoding.UTF8.GetString(ms.ToArray()),
            contentType: "application/json", statusCode: status);
    }

    private static IResult JsonError(int status, string error, string description) =>
        OAuthError(status, error, description);
}
