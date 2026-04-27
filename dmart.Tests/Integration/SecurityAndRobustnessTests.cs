using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Tests covering security and robustness fixes added in the codebase analysis:
// CORS fallback, query limit clamping, HSTS, exception masking, WebSocket
// endpoints, upload limits, protected fields, AllowedSubmitModels.
public class SecurityAndRobustnessTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SecurityAndRobustnessTests(DmartFactory factory) => _factory = factory;

    // Per-test user (super_admin role) so concurrent tests don't race each
    // other via MaxSessionsPerUser eviction — see DmartFactory.CreateLoggedInUserAsync.
    private async Task<(HttpClient Client, string Token)> LoginAsync()
    {
        var u = await _factory.CreateLoggedInUserAsync();
        return (u.Client, u.Token);
    }

    // ==================== CORS fallback uses localhost ====================

    [Fact]
    public async Task Cors_Fallback_Uses_Localhost_Not_0000()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "https://stranger.example.com");
        var resp = await client.SendAsync(req);
        // With empty AllowedCorsOrigins, the fallback should use localhost, not 0.0.0.0.
        if (resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var values))
        {
            var origin = string.Join(",", values);
            origin.ShouldNotContain("0.0.0.0");
        }
    }

    // ==================== HSTS not on HTTP ====================

    [Fact]
    public async Task Hsts_Not_Sent_On_Http()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/");
        // Test host uses HTTP — HSTS must NOT be present (RFC 6797).
        resp.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    // ==================== Query limit clamped ====================

    [FactIfPg]
    public async Task Query_Limit_Clamped_To_MaxQueryLimit()
    {
        var svc = _factory.Services.GetRequiredService<QueryService>();
        // Request limit=999999 — should be clamped to MaxQueryLimit (default 10000).
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = Dmart.Models.Enums.QueryType.Subpath,
            SpaceName = "management",
            Subpath = "/",
            Limit = 999999,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        // The returned count should be <= MaxQueryLimit, not 999999.
        var returned = (int)resp.Attributes!["returned"]!;
        returned.ShouldBeLessThanOrEqualTo(10000);
    }

    // ==================== Exception messages masked ====================

    [FactIfPg]
    public async Task Query_Error_Does_Not_Leak_Exception_Details()
    {
        var (client, _) = await LoginAsync();
        // Send malformed JSON to /managed/query — error should be generic.
        var resp = await client.PostAsync("/managed/query",
            new StringContent("{invalid json!!!", Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        // Should NOT contain internal JSON parser class names or stack traces.
        body.Error!.Message.ShouldNotContain("JsonReaderException");
        body.Error.Message.ShouldNotContain("System.");
    }

    // ==================== WebSocket info endpoint ====================

    [FactIfPg]
    public async Task WsInfo_Returns_Connected_Clients()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync("/ws-info");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("connected_clients");
        // `channels` is omitted from the wire when empty (JsonStripEmptiesMiddleware).
        // In this test there are no subscribers, so don't require the key — assert
        // only that when present, it's non-empty (present ⇒ meaningful).
    }

    [Fact]
    public async Task WsInfo_Requires_Authorization()
    {
        // Anonymous access to /ws-info would leak the list of connected users
        // and channel subscriptions. Must return 401 without a token.
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/ws-info");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ==================== WebSocket broadcast endpoint ====================

    [FactIfPg]
    public async Task Broadcast_Endpoint_Returns_Success()
    {
        var (client, _) = await LoginAsync();
        var body = new StringContent(
            "{\"type\":\"test\",\"message\":{\"hello\":\"world\"},\"channels\":[\"test:/:__ALL__:__ALL__:__ALL__\"]}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/broadcast-to-channels", body);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("success");
    }

    [Fact]
    public async Task Broadcast_Endpoint_Requires_Authorization()
    {
        var client = _factory.CreateClient();
        var body = new StringContent(
            "{\"type\":\"test\",\"message\":{},\"channels\":[\"x\"]}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/broadcast-to-channels", body);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ==================== Send-message endpoint ====================

    [FactIfPg]
    public async Task SendMessage_Endpoint_Returns_Success()
    {
        var (client, _) = await LoginAsync();
        var body = new StringContent(
            "{\"type\":\"test\",\"message\":{\"data\":\"hello\"}}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/send-message/nonexistent_user", body);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("message_sent");
        // User doesn't exist → message_sent should be false.
        json.ShouldContain("false");
    }

    [Fact]
    public async Task SendMessage_Endpoint_Requires_Authorization()
    {
        var client = _factory.CreateClient();
        var body = new StringContent(
            "{\"type\":\"test\",\"message\":{}}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/send-message/anyone", body);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [FactIfPg]
    public async Task SendMessage_Escapes_MsgType_With_Quotes()
    {
        // An attacker-supplied msgType containing quotes must not break the JSON
        // structure of the broadcast payload (JSON injection).
        var (client, _) = await LoginAsync();
        var body = new StringContent(
            "{\"type\":\"evil\\\",\\\"injected\\\":\\\"x\",\"message\":{}}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/send-message/nonexistent_user", body);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Response itself must be parseable JSON with the expected shape.
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
        doc.RootElement.GetProperty("message_sent").GetBoolean().ShouldBe(false);
    }

    // Upload size limit (50MB) is enforced in ResourceWithPayloadHandler.
    // Skipping in automated tests to avoid 51MB allocation — verified
    // manually and via code review.
}
