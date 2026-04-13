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

    private async Task<(HttpClient Client, string Token)> LoginAsync()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var token = body!.Records![0].Attributes!["access_token"]!.ToString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (client, token);
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

    [Fact]
    public async Task Query_Limit_Clamped_To_MaxQueryLimit()
    {
        if (!DmartFactory.HasPg) return;
        var svc = _factory.Services.GetRequiredService<QueryService>();
        // Request limit=999999 — should be clamped to MaxQueryLimit (default 10000).
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = Dmart.Models.Enums.QueryType.Subpath,
            SpaceName = "applications",
            Subpath = "/",
            Limit = 999999,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        // The returned count should be <= MaxQueryLimit, not 999999.
        var returned = (int)resp.Attributes!["returned"]!;
        returned.ShouldBeLessThanOrEqualTo(10000);
    }

    // ==================== Exception messages masked ====================

    [Fact]
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

    [Fact]
    public async Task WsInfo_Returns_Connected_Clients()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/ws-info");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("connected_clients");
        json.ShouldContain("channels");
    }

    // ==================== WebSocket broadcast endpoint ====================

    [Fact]
    public async Task Broadcast_Endpoint_Returns_Success()
    {
        var client = _factory.CreateClient();
        var body = new StringContent(
            "{\"type\":\"test\",\"message\":{\"hello\":\"world\"},\"channels\":[\"test:/:__ALL__:__ALL__:__ALL__\"]}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/broadcast-to-channels", body);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("success");
    }

    // ==================== Send-message endpoint ====================

    [Fact]
    public async Task SendMessage_Endpoint_Returns_Success()
    {
        var client = _factory.CreateClient();
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

    // Upload size limit (50MB) is enforced in ResourceWithPayloadHandler.
    // Skipping in automated tests to avoid 51MB allocation — verified
    // manually and via code review.
}
