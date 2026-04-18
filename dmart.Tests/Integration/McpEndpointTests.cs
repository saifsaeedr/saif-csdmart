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

    // ---- v0.2 write-path round trip ----

    [Fact]
    public async Task CreateReadUpdateDelete_RoundTrip()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var shortname = $"mcp_rt_{suffix}";
        var space = "management";
        var subpath = "/";
        var resourceType = "content";

        try
        {
            // 1. Create
            var createResp = await client.PostAsync("/mcp", JsonRpc(
                "tools/call", id: 100,
                paramsJson: $$"""
                {
                  "name": "dmart.create",
                  "arguments": {
                    "space_name": "{{space}}",
                    "subpath": "{{subpath}}",
                    "shortname": "{{shortname}}",
                    "resource_type": "{{resourceType}}"
                  }
                }
                """));
            createResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var createBody = await ReadJson(createResp);
            var createdText = createBody.GetProperty("result").GetProperty("content")[0]
                .GetProperty("text").GetString()!;
            using (var created = JsonDocument.Parse(createdText))
            {
                created.RootElement.GetProperty("status").GetString().ShouldBe("created");
                created.RootElement.GetProperty("shortname").GetString().ShouldBe(shortname);
            }

            // 2. Read back via dmart.read — confirm it's there.
            var readResp = await client.PostAsync("/mcp", JsonRpc(
                "tools/call", id: 101,
                paramsJson: $$"""
                {
                  "name": "dmart.read",
                  "arguments": {
                    "space_name": "{{space}}",
                    "subpath": "{{subpath}}",
                    "shortname": "{{shortname}}",
                    "resource_type": "{{resourceType}}"
                  }
                }
                """));
            var readBody = await ReadJson(readResp);
            var readText = readBody.GetProperty("result").GetProperty("content")[0]
                .GetProperty("text").GetString()!;
            using (var readDoc = JsonDocument.Parse(readText))
            {
                var records = readDoc.RootElement.GetProperty("records");
                records.GetArrayLength().ShouldBe(1);
                records[0].GetProperty("shortname").GetString().ShouldBe(shortname);
            }

            // 3. Update — add a tag via the patch payload.
            var updateResp = await client.PostAsync("/mcp", JsonRpc(
                "tools/call", id: 102,
                paramsJson: $$"""
                {
                  "name": "dmart.update",
                  "arguments": {
                    "space_name": "{{space}}",
                    "subpath": "{{subpath}}",
                    "shortname": "{{shortname}}",
                    "resource_type": "{{resourceType}}",
                    "patch": { "tags": ["mcp-rt"] }
                  }
                }
                """));
            var updateBody = await ReadJson(updateResp);
            var updatedText = updateBody.GetProperty("result").GetProperty("content")[0]
                .GetProperty("text").GetString()!;
            using (var updated = JsonDocument.Parse(updatedText))
            {
                updated.RootElement.GetProperty("status").GetString().ShouldBe("updated");
            }

            // 4. Delete WITHOUT confirm — rejected.
            var deleteNoConfirmResp = await client.PostAsync("/mcp", JsonRpc(
                "tools/call", id: 103,
                paramsJson: $$"""
                {
                  "name": "dmart.delete",
                  "arguments": {
                    "space_name": "{{space}}",
                    "subpath": "{{subpath}}",
                    "shortname": "{{shortname}}",
                    "resource_type": "{{resourceType}}"
                  }
                }
                """));
            var noConfirmBody = await ReadJson(deleteNoConfirmResp);
            var noConfirmResult = noConfirmBody.GetProperty("result");
            noConfirmResult.GetProperty("isError").GetBoolean().ShouldBeTrue();

            // 5. Delete WITH confirm — succeeds.
            var deleteResp = await client.PostAsync("/mcp", JsonRpc(
                "tools/call", id: 104,
                paramsJson: $$"""
                {
                  "name": "dmart.delete",
                  "arguments": {
                    "space_name": "{{space}}",
                    "subpath": "{{subpath}}",
                    "shortname": "{{shortname}}",
                    "resource_type": "{{resourceType}}",
                    "confirm": true
                  }
                }
                """));
            var deleteBody = await ReadJson(deleteResp);
            var deletedText = deleteBody.GetProperty("result").GetProperty("content")[0]
                .GetProperty("text").GetString()!;
            using (var deleted = JsonDocument.Parse(deletedText))
            {
                deleted.RootElement.GetProperty("status").GetString().ShouldBe("deleted");
            }
        }
        finally
        {
            // Best-effort cleanup if a test step failed mid-flow.
            try
            {
                var cleanup = "{\"name\":\"dmart.delete\",\"arguments\":"
                    + $"{{\"space_name\":\"{space}\",\"subpath\":\"{subpath}\","
                    + $"\"shortname\":\"{shortname}\",\"resource_type\":\"{resourceType}\","
                    + "\"confirm\":true}}";
                await client.PostAsync("/mcp", JsonRpc("tools/call", id: 999, paramsJson: cleanup));
            }
            catch { /* swallow — best effort */ }
        }
    }

    // ---- v0.3 ----

    [Fact]
    public async Task History_Returns_CreateEvent()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var shortname = $"mcp_hist_{suffix}";
        var space = "management";
        var subpath = "/";
        var resourceType = "content";

        try
        {
            // Seed an entry — history for this is the create record.
            var createCall = "{\"name\":\"dmart.create\",\"arguments\":"
                + $"{{\"space_name\":\"{space}\",\"subpath\":\"{subpath}\","
                + $"\"shortname\":\"{shortname}\",\"resource_type\":\"{resourceType}\""
                + "}}";
            var createResp = await client.PostAsync("/mcp",
                JsonRpc("tools/call", id: 200, paramsJson: createCall));
            createResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var histCall = "{\"name\":\"dmart.history\",\"arguments\":"
                + $"{{\"space_name\":\"{space}\",\"subpath\":\"{subpath}\","
                + $"\"shortname\":\"{shortname}\""
                + "}}";
            var histResp = await client.PostAsync("/mcp",
                JsonRpc("tools/call", id: 201, paramsJson: histCall));
            histResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await ReadJson(histResp);
            var text = body.GetProperty("result").GetProperty("content")[0]
                .GetProperty("text").GetString()!;
            using var doc = JsonDocument.Parse(text);
            doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
            doc.RootElement.GetProperty("records").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            var cleanup = "{\"name\":\"dmart.delete\",\"arguments\":"
                + $"{{\"space_name\":\"{space}\",\"subpath\":\"{subpath}\","
                + $"\"shortname\":\"{shortname}\",\"resource_type\":\"{resourceType}\","
                + "\"confirm\":true}}";
            try { await client.PostAsync("/mcp", JsonRpc("tools/call", id: 998, paramsJson: cleanup)); }
            catch { }
        }
    }

    [Fact]
    public async Task SemanticSearch_Returns_Clean_Response_Regardless_Of_Environment()
    {
        // Covers three scenarios with one assertion:
        //   (a) pgvector not installed → Response with status=failed +
        //       error.type="request" and a "not configured" message.
        //   (b) pgvector present, EMBEDDING_API_URL blank → same result.
        //   (c) Both configured + a real embedder → status=success with
        //       records (possibly empty).
        // Every scenario returns a well-formed Response envelope inside a
        // successful MCP ToolsCallResult — never a raw exception.
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var resp = await client.PostAsync("/mcp", JsonRpc(
            "tools/call", id: 220,
            paramsJson: """{"name":"dmart.semantic_search","arguments":{"query":"anything"}}"""));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJson(resp);
        var result = body.GetProperty("result");
        result.TryGetProperty("isError", out var isErr).ShouldBeTrue();
        isErr.GetBoolean().ShouldBeFalse();

        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var doc = JsonDocument.Parse(text);
        var status = doc.RootElement.GetProperty("status").GetString();
        status.ShouldBeOneOf("success", "failed");
        if (status == "failed")
        {
            var msg = doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            // One of the two disabled diagnostics — "not configured" (no
            // provider URL) or "not available" (no pgvector extension).
            (msg.Contains("not configured") || msg.Contains("not available"))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Download_UnknownEntry_ReturnsToolError()
    {
        if (!DmartFactory.HasPg) return;
        using var client = await LoginClient();

        var resp = await client.PostAsync("/mcp", JsonRpc(
            "tools/call", id: 210,
            paramsJson: """
            {"name":"dmart.download","arguments":{"space_name":"management","shortname":"definitely_nonexistent_entry_mcp","resource_type":"content"}}
            """));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJson(resp);
        var result = body.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
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
