using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Integration tests for the in-process MCP endpoint. Covers the JSON-RPC
// spec surface + auth gating + Python-parity permission enforcement.
// Uses real HTTP against a WebApplicationFactory host; authenticates with
// the bootstrap admin JWT via the existing /user/login flow.
public sealed class McpEndpointTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public McpEndpointTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Unauthenticated_Rejected()
    {
        if (!DmartFactory.HasPg) return;
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/mcp", JsonRpc("initialize", id: 1));
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Initialize_Returns_ProtocolVersion_And_ServerInfo()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var resp = await client.PostAsync("/mcp", JsonRpc(
            "initialize",
            id: 1,
            paramsJson: """{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"xunit","version":"0"}}"""));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Headers.TryGetValues("Mcp-Session-Id", out var sid).ShouldBeTrue();
        sid!.First().Length.ShouldBeGreaterThan(0);

        var root = await ReadJson(resp);
        root.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        root.GetProperty("id").GetInt32().ShouldBe(1);
        var result = root.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().ShouldBe("2025-03-26");
        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("dmart");
        result.GetProperty("capabilities").GetProperty("tools").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task ToolsList_Returns_Registered_Tools()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var resp = await client.PostAsync("/mcp", JsonRpc("tools/list", id: 2));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await ReadJson(resp);
        var tools = root.GetProperty("result").GetProperty("tools");
        tools.ValueKind.ShouldBe(JsonValueKind.Array);
        tools.GetArrayLength().ShouldBeGreaterThanOrEqualTo(5);

        var names = new List<string>();
        foreach (var tool in tools.EnumerateArray())
            names.Add(tool.GetProperty("name").GetString()!);
        names.ShouldContain("dmart.me");
        names.ShouldContain("dmart.spaces");
        names.ShouldContain("dmart.query");
        names.ShouldContain("dmart.read");
        names.ShouldContain("dmart.schema");

        // Every tool carries an input schema.
        foreach (var tool in tools.EnumerateArray())
            tool.GetProperty("inputSchema").GetProperty("type").GetString().ShouldBe("object");
    }

    [Fact]
    public async Task ToolsCall_Me_Returns_Caller_Identity()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var resp = await client.PostAsync("/mcp", JsonRpc(
            "tools/call", id: 3,
            paramsJson: """{"name":"dmart.me","arguments":{}}"""));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await ReadJson(resp);
        var content = root.GetProperty("result").GetProperty("content")[0];
        content.GetProperty("type").GetString().ShouldBe("text");

        using var inner = JsonDocument.Parse(content.GetProperty("text").GetString()!);
        inner.RootElement.GetProperty("shortname").GetString().ShouldBe(_factory.AdminShortname);
        inner.RootElement.GetProperty("type").GetString().ShouldBe("web");
        inner.RootElement.GetProperty("accessible").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_Returns_JsonRpc_Error()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var resp = await client.PostAsync("/mcp", JsonRpc(
            "tools/call", id: 4,
            paramsJson: """{"name":"dmart.nope","arguments":{}}"""));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);   // JSON-RPC error lives in the body, not the HTTP status
        var root = await ReadJson(resp);
        root.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
    }

    [Fact]
    public async Task UnknownMethod_Returns_MethodNotFound()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var resp = await client.PostAsync("/mcp", JsonRpc("totally/fake", id: 5));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await ReadJson(resp);
        root.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
    }

    [Fact]
    public async Task Notification_Returns_202_NoBody()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        // No `id` field — spec-defined notification; server must not reply.
        var resp = await client.PostAsync("/mcp",
            Payload("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        resp.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldBeEmpty();
    }

    // ---- helpers ----

    private async Task<HttpClient> LoginClient()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login,
            DmartJsonContext.Default.UserLoginRequest);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var token = body!.Records!.First().Attributes!["access_token"].ToString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent JsonRpc(string method, int id, string? paramsJson = null)
    {
        var body = paramsJson is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""";
        return Payload(body);
    }

    private static StringContent Payload(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
