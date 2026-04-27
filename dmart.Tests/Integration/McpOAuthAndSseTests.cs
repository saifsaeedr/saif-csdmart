using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Api.Mcp;
using Dmart.Auth;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Integration tests for the v0.5 MCP additions: the OAuth 2.1 transport
// (discovery / dynamic client registration / authorize / token with PKCE),
// the SSE server→client stream, and the elicitation/create confirmation
// flow that the delete tool drives when the client opts in.
public sealed class McpOAuthAndSseTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public McpOAuthAndSseTests(DmartFactory factory) => _factory = factory;

    // ---- Discovery ----

    [FactIfPg]
    public async Task ProtectedResourceMetadata_Advertises_Authorization_Server()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/.well-known/oauth-protected-resource");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await ReadJson(resp);
        root.GetProperty("authorization_servers").GetArrayLength().ShouldBe(1);
        var scopes = root.GetProperty("scopes_supported");
        scopes.GetArrayLength().ShouldBeGreaterThan(0);
        scopes[0].GetString().ShouldBe("mcp");
        // `resource` must identify the MCP endpoint itself, not the bare origin —
        // MCP clients use it to correlate tokens with the specific resource.
        root.GetProperty("resource").GetString().ShouldEndWith("/mcp");
    }

    [FactIfPg]
    public async Task Authorize_Form_Posts_Back_To_Current_Url()
    {
        using var client = _factory.CreateClient();

        const string redirectUri = "http://localhost/form-action-test/callback";
        var reg = await client.PostAsync("/oauth/register",
            new StringContent(
                $$"""{"redirect_uris":["{{redirectUri}}"],"client_name":"form-action"}""",
                Encoding.UTF8, "application/json"));
        var clientId = (await ReadJson(reg)).GetProperty("client_id").GetString()!;

        var page = await client.GetStringAsync(
            $"/oauth/authorize?response_type=code&client_id={clientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&code_challenge=abc123&code_challenge_method=S256&state=xyz&scope=mcp");

        // The form MUST post back to the same URL (empty action). An absolute
        // "/oauth/authorize" breaks when dmart is mounted under a sub-path —
        // the POST lands on the origin root, bypassing the reverse proxy
        // rule that routes /<prefix>/oauth/* to dmart.
        page.ShouldContain("action=\"\"");
        page.ShouldNotContain("action=\"/oauth/authorize\"");
    }

    [FactIfPg]
    public async Task Unauthenticated_Mcp_401_Carries_WWWAuthenticate_With_ResourceMetadata()
    {
        using var client = _factory.CreateClient();

        // No Authorization header → JwtBearer challenges. The response MUST
        // carry `WWW-Authenticate: Bearer resource_metadata=...` per MCP's
        // authorization profile — without it, Zed / Cursor / Claude Desktop
        // can't kick off OAuth discovery.
        var resp = await client.PostAsync("/mcp", new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
            Encoding.UTF8, "application/json"));
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        resp.Headers.TryGetValues("WWW-Authenticate", out var values).ShouldBeTrue();
        var header = values!.First();
        header.ShouldStartWith("Bearer ");
        header.ShouldContain("realm=\"dmart-mcp\"");
        header.ShouldContain("resource_metadata=");
        header.ShouldContain("/.well-known/oauth-protected-resource");
    }

    [FactIfPg]
    public async Task AuthorizationServerMetadata_Declares_Pkce_Only_Public_Clients()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/.well-known/oauth-authorization-server");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await ReadJson(resp);

        root.GetProperty("authorization_endpoint").GetString().ShouldEndWith("/oauth/authorize");
        root.GetProperty("token_endpoint").GetString().ShouldEndWith("/oauth/token");
        root.GetProperty("registration_endpoint").GetString().ShouldEndWith("/oauth/register");

        // S256 is the only PKCE method we accept.
        var methods = root.GetProperty("code_challenge_methods_supported");
        methods.GetArrayLength().ShouldBe(1);
        methods[0].GetString().ShouldBe("S256");

        // Public clients only — token_endpoint_auth_methods must include "none".
        var authMethods = root.GetProperty("token_endpoint_auth_methods_supported");
        var names = new List<string>();
        foreach (var m in authMethods.EnumerateArray()) names.Add(m.GetString()!);
        names.ShouldContain("none");
    }

    // ---- Dynamic client registration + authorize + token ----

    [FactIfPg]
    public async Task FullOauthCodeFlow_With_Pkce_Issues_Valid_JwtAccessToken()
    {
        // Per-test user — see DmartFactory.CreateTestUserAsync. Avoids the
        // MaxSessionsPerUser eviction race when many tests log in as admin.
        var creds = await _factory.CreateTestUserAsync();

        using var client = _factory.CreateClient();
        // Disable auto-redirects so the /oauth/authorize POST's 302 is visible.
        using var noRedirect = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });

        // 1. Register the client.
        const string redirectUri = "http://localhost/mcp-client/callback";
        var reg = await client.PostAsync("/oauth/register",
            new StringContent(
                $$"""{"redirect_uris":["{{redirectUri}}"],"client_name":"test-client"}""",
                Encoding.UTF8, "application/json"));
        reg.StatusCode.ShouldBe(HttpStatusCode.Created);
        var regBody = await ReadJson(reg);
        var clientId = regBody.GetProperty("client_id").GetString()!;
        clientId.ShouldNotBeNullOrEmpty();

        // 2. Build PKCE challenge.
        var verifier = CreatePkceVerifier();
        var challenge = S256Challenge(verifier);
        var state = "xunit-state-1234";

        // 3. POST /oauth/authorize with the per-test user credentials.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "mcp",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["shortname"] = creds.Shortname,
            ["password"] = creds.Password,
        });
        var authResp = await noRedirect.PostAsync("/oauth/authorize", form);
        authResp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = authResp.Headers.Location!.ToString();
        location.ShouldStartWith(redirectUri);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
        query["state"].ShouldBe(state);
        var code = query["code"]!;
        code.ShouldNotBeNullOrEmpty();

        // 4. Exchange code for tokens.
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        });
        var tokenResp = await client.PostAsync("/oauth/token", tokenForm);
        tokenResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokenBody = await ReadJson(tokenResp);
        tokenBody.GetProperty("token_type").GetString().ShouldBe("Bearer");
        var accessToken = tokenBody.GetProperty("access_token").GetString()!;
        accessToken.ShouldNotBeNullOrEmpty();
        tokenBody.GetProperty("refresh_token").GetString().ShouldNotBeNullOrEmpty();

        // 5. Use the token against /mcp — the whole point of this flow.
        using var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var ping = await authed.PostAsync("/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""",
                Encoding.UTF8, "application/json"));
        ping.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [FactIfPg]
    public async Task Token_With_Wrong_Verifier_Is_Rejected()
    {
        using var client = _factory.CreateClient();
        var noRedirect = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });

        const string redirectUri = "http://localhost/mcp-client/callback2";
        var reg = await client.PostAsync("/oauth/register",
            new StringContent(
                $$"""{"redirect_uris":["{{redirectUri}}"],"client_name":"t2"}""",
                Encoding.UTF8, "application/json"));
        var clientId = (await ReadJson(reg)).GetProperty("client_id").GetString()!;

        var verifier = CreatePkceVerifier();
        var challenge = S256Challenge(verifier);

        var creds = await _factory.CreateTestUserAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = "x",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["shortname"] = creds.Shortname,
            ["password"] = creds.Password,
        });
        var authResp = await noRedirect.PostAsync("/oauth/authorize", form);
        var location = authResp.Headers.Location!.ToString();
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["code"]!;

        var wrongVerifier = CreatePkceVerifier();  // different value
        var tokenResp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = wrongVerifier,
            }));
        tokenResp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await ReadJson(tokenResp);
        body.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    [FactIfPg]
    public async Task Authorize_With_Unknown_Client_Is_Rejected()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=mcp_nope&redirect_uri=http://localhost/cb&code_challenge=abc");
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [FactIfPg]
    public async Task RefreshToken_Issues_New_Access()
    {
        // /user/login follows Python dmart's wire contract and does NOT
        // return a refresh_token — only the /oauth/token endpoint does,
        // for MCP OAuth clients. Go through the authorization_code grant
        // to obtain a refresh_token, then exercise the refresh_token grant.
        using var client = _factory.CreateClient();
        using var noRedirect = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });

        const string redirectUri = "http://localhost/mcp-client/refresh-cb";
        var reg = await client.PostAsync("/oauth/register",
            new StringContent(
                $$"""{"redirect_uris":["{{redirectUri}}"],"client_name":"refresh-test"}""",
                Encoding.UTF8, "application/json"));
        var clientId = (await ReadJson(reg)).GetProperty("client_id").GetString()!;

        var creds = await _factory.CreateTestUserAsync();
        var verifier = CreatePkceVerifier();
        var authResp = await noRedirect.PostAsync("/oauth/authorize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["state"] = "refresh-state",
                ["code_challenge"] = S256Challenge(verifier),
                ["code_challenge_method"] = "S256",
                ["shortname"] = creds.Shortname,
                ["password"] = creds.Password,
            }));
        var code = System.Web.HttpUtility.ParseQueryString(
            new Uri(authResp.Headers.Location!.ToString()).Query)["code"]!;

        var firstTokenResp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }));
        firstTokenResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refresh = (await ReadJson(firstTokenResp)).GetProperty("refresh_token").GetString()!;

        var tokenResp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
            }));
        tokenResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJson(tokenResp);
        body.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
    }

    // ---- SSE + elicitation ----

    [FactIfPg]
    public async Task SseGet_WithoutInitialize_Returns_BadRequest()
    {
        using var client = await LoginClient();
        var resp = await client.GetAsync("/mcp");
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [FactIfPg]
    public async Task Elicitation_Declined_Delete_Cancels_Without_Throwing()
    {
        // This is a focused unit-level test on McpElicitation — we push an
        // "action=decline" result into the pending TCS directly and verify the
        // DeleteAsync path routes through BuildDeclinedResponse rather than
        // performing the deletion.
        var store = _factory.Services.GetRequiredService<McpSessionStore>();
        var session = store.Create("xunit", "0", "2025-03-26");
        session.UserShortname = "dmart";
        session.ElicitationSupported = true;

        // Drain any frames the bridge plugin may have enqueued before.
        while (session.Outbox.Reader.TryRead(out _)) { }

        // Simulate: tool emits elicitation/create, client replies decline.
        var waiter = Task.Run(async () =>
        {
            // Wait for the elicitation/create frame to land.
            var frame = await session.Outbox.Reader.ReadAsync();
            using var doc = JsonDocument.Parse(frame);
            var requestId = doc.RootElement.GetProperty("id").GetString()!;
            requestId.ShouldNotBeNullOrEmpty();

            // Resolve the TCS with an "action=decline" response as if the
            // client had POSTed back.
            session.PendingElicitations.TryGetValue(requestId, out var tcs).ShouldBeTrue();
            var decline = JsonDocument.Parse("""{"action":"decline"}""").RootElement.Clone();
            tcs!.TrySetResult(decline);
        });

        var httpCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = _factory.Services,
        };
        httpCtx.Request.Headers["Mcp-Session-Id"] = session.Id;
        var outcome = await McpElicitation.TryConfirmDeleteAsync(
            httpCtx, "management", "/users", "xunit_target",
            Dmart.Models.Enums.ResourceType.Content, default);
        await waiter;
        outcome.ShouldBe(McpElicitation.Outcome.Declined);
    }

    [FactIfPg]
    public async Task Elicitation_Accepted_Returns_Accepted_Outcome()
    {
        var store = _factory.Services.GetRequiredService<McpSessionStore>();
        var session = store.Create("xunit-accept", "0", "2025-03-26");
        session.UserShortname = "dmart";
        session.ElicitationSupported = true;
        while (session.Outbox.Reader.TryRead(out _)) { }

        var waiter = Task.Run(async () =>
        {
            var frame = await session.Outbox.Reader.ReadAsync();
            using var doc = JsonDocument.Parse(frame);
            var requestId = doc.RootElement.GetProperty("id").GetString()!;
            session.PendingElicitations.TryGetValue(requestId, out var tcs).ShouldBeTrue();
            var accept = JsonDocument.Parse(
                """{"action":"accept","content":{"confirm":true}}""").RootElement.Clone();
            tcs!.TrySetResult(accept);
        });

        var httpCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = _factory.Services,
        };
        httpCtx.Request.Headers["Mcp-Session-Id"] = session.Id;
        var outcome = await McpElicitation.TryConfirmDeleteAsync(
            httpCtx, "management", "/users", "xunit_target",
            Dmart.Models.Enums.ResourceType.Content, default);
        await waiter;
        outcome.ShouldBe(McpElicitation.Outcome.Accepted);
    }

    // ---- PKCE primitives ----

    private static string CreatePkceVerifier()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string S256Challenge(string verifier)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.ASCII.GetBytes(verifier), hash);
        return Convert.ToBase64String(hash)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // ---- helpers ----

    // Per-test user — see DmartFactory.CreateLoggedInUserAsync.
    private async Task<HttpClient> LoginClient()
    {
        var u = await _factory.CreateLoggedInUserAsync();
        return u.Client;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
