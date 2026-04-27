using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// In-process twin of curl.sh. Runs the same 32 end-to-end scenarios against
// WebApplicationFactory<Program> so coverlet sees the Api → Services → DataAdapters
// execution that curl.sh exercises out-of-process (and therefore doesn't instrument).
//
// Uses raw JSON strings instead of the C# model DTOs for request bodies so the wire
// payloads match curl.sh byte-for-byte. Uses a distinct space name ("itest_scenario")
// so it doesn't collide with curl.sh's "dummy" space if both are run against the
// same database.
//
// The space/shortname/subpath names are inlined as literals rather than using
// `const` + `$$` interpolation, because the JSON bodies contain `}}` sequences
// (e.g. `"attributes":{}}}]}` at the tail) that tangle with raw-string
// interpolation terminators. Literal strings keep the JSON intact.
public class CurlScenarioTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public CurlScenarioTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Full_Curl_Sh_Scenario_End_To_End()
    {
        // Per-test user with super_admin role — see DmartFactory.CreateLoggedInUserAsync.
        // Login here is done inline (not via the helper) because this scenario
        // asserts the literal Set-Cookie shape and the JWT claim values from
        // /user/login, so it has to inspect the response itself.
        var actorShortname = $"itest_{Guid.NewGuid():N}"[..16];
        const string actorPassword = "TestPassword1";
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = actorShortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = actorShortname,
            IsActive = true,
            Password = hasher.Hash(actorPassword),
            Type = UserType.Web,
            Language = Language.En,
            Roles = new() { "super_admin" },
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var client = _factory.CreateClient();

        // ---------------------------------------------------------------------
        // 1. Login — returns access_token in attributes AND a Set-Cookie header.
        // ---------------------------------------------------------------------
        var loginBodyJson =
            "{\"shortname\":\"" + actorShortname +
            "\",\"password\":\"" + actorPassword + "\"}";
        var loginResp = await client.PostAsync("/user/login", Json(loginBodyJson));
        var loginRaw = await loginResp.Content.ReadAsStringAsync();
        var loginBody = JsonSerializer.Deserialize(loginRaw, DmartJsonContext.Default.Response);
        loginBody.ShouldNotBeNull($"Login deserialization failed: {loginRaw}");
        loginBody!.Status.ShouldBe(Status.Success, $"Login failed: {loginRaw}");
        var token = loginBody.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"Login failed for '{actorShortname}': {loginResp.StatusCode} {loginRaw}");
        token.ShouldNotBeNullOrEmpty();

        // Cookie should also be set (dmart parity check from curl.sh).
        loginResp.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeTrue();
        cookies!.Any(c => c.StartsWith("auth_token=", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // ---------------------------------------------------------------------
        // 2. JWT header decodes and alg == HS256.
        // ---------------------------------------------------------------------
        var headerSeg = token.Split('.')[0].Replace('-', '+').Replace('_', '/');
        headerSeg += new string('=', (4 - headerSeg.Length % 4) % 4);
        var headerJson = Encoding.UTF8.GetString(Convert.FromBase64String(headerSeg));
        using (var doc = JsonDocument.Parse(headerJson))
            doc.RootElement.GetProperty("alg").GetString().ShouldBe("HS256");

        // 2b. JWT payload contains Python-compatible data + standard claims.
        var payloadSeg = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payloadSeg += new string('=', (4 - payloadSeg.Length % 4) % 4);
        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(payloadSeg));
        using (var pdoc = JsonDocument.Parse(payloadJson))
        {
            var p = pdoc.RootElement;
            // Standard claims
            p.GetProperty("sub").GetString().ShouldBe(actorShortname);
            p.TryGetProperty("exp", out _).ShouldBeTrue();
            p.TryGetProperty("iss", out _).ShouldBeTrue();
            p.TryGetProperty("aud", out _).ShouldBeTrue();
            // Python-compatible claims
            p.TryGetProperty("data", out var data).ShouldBeTrue("JWT missing data object");
            data.GetProperty("shortname").GetString().ShouldBe(actorShortname);
            data.TryGetProperty("type", out _).ShouldBeTrue("JWT data missing type");
            p.TryGetProperty("expires", out var expires).ShouldBeTrue("JWT missing expires");
            expires.GetInt64().ShouldBe(p.GetProperty("exp").GetInt64());
        }

        // ---------------------------------------------------------------------
        // 3. Profile
        // ---------------------------------------------------------------------
        (await GetResponse(client, "/user/profile")).Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 4. Cleanup any lingering test space from a previous run (may fail).
        // ---------------------------------------------------------------------
        await PostJson(client, "/managed/request", DeleteSpaceBody);

        // ---------------------------------------------------------------------
        // 5. Create test space
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request",
            """{"space_name":"itest_scenario","request_type":"create","records":[{"resource_type":"space","subpath":"/","shortname":"itest_scenario","attributes":{"hide_space":true,"is_active":true}}]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 6. Query spaces
        // dmart Python restricts type=spaces to space_name == management, so
        // we query management/ to list every space the actor can see — which
        // will include the itest_scenario space we just created.
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/query",
            """{"space_name":"management","type":"spaces","subpath":"/"}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 7. Create folders (four of them)
        // ---------------------------------------------------------------------
        foreach (var folder in new[] { "myfolder", "posts", "workflows", "schema" })
        {
            var body =
                "{\"space_name\":\"itest_scenario\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"" + folder +
                "\",\"attributes\":{\"is_active\":true,\"tags\":[\"one\",\"two\"]}}]}";
            var fResp = await PostJsonAsResponse(client, "/managed/request", body);
            // /schema may already exist (auto-created by resource_folders_creation plugin)
            if (folder == "schema" && fResp.Status == Status.Failed) continue;
            fResp.Status.ShouldBe(Status.Success, $"create folder {folder}");
        }

        // ---------------------------------------------------------------------
        // 8. Query folders
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/query",
            """{"space_name":"itest_scenario","type":"subpath","subpath":"/","filter_schema_names":[]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 9. Create workflow schema (multipart)
        // ---------------------------------------------------------------------
        (await UploadAsync(client,
            record: """{"resource_type":"schema","subpath":"schema","shortname":"ticket_workflows","attributes":{"payload":{"content_type":"json","body":"workflow_schema.json"}}}""",
            payloadBytes: Utf8("""{"title":"Ticket Workflow Schema","type":"object","additionalProperties":true,"properties":{"initial_state":{"type":"string"},"states":{"type":"array","items":{"type":"object","properties":{"state":{"type":"string"},"next":{"type":"array"}}}}}}"""),
            payloadFileName: "workflow_schema.json",
            payloadMime: "application/json"))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 10. Create test_schema (multipart)
        // ---------------------------------------------------------------------
        (await UploadAsync(client,
            record: """{"resource_type":"schema","subpath":"schema","shortname":"test_schema","attributes":{"payload":{"content_type":"json","body":"schema.json"}}}""",
            payloadBytes: Utf8("""{"title":"My nice schema","type":"object","properties":{"name":{"type":"string"},"price":{"type":"number"}},"required":["name"]}"""),
            payloadFileName: "schema.json",
            payloadMime: "application/json"))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 11. Create workflow definition (multipart, uses ticket_workflows schema)
        // ---------------------------------------------------------------------
        (await UploadAsync(client,
            record: """{"resource_type":"content","subpath":"/workflows","shortname":"myworkflow","attributes":{"is_active":true,"payload":{"schema_shortname":"ticket_workflows","content_type":"json","body":"ticket_workflow.json"}}}""",
            payloadBytes: Utf8("""{"initial_state":"draft","states":[{"state":"draft","next":[{"action":"submit","to":"submitted"}]},{"state":"submitted","next":[{"action":"approve","to":"approved"},{"action":"reject","to":"rejected"}]}],"closed_states":["approved","rejected"]}"""),
            payloadFileName: "ticket_workflow.json",
            payloadMime: "application/json"))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 12. Create ticket (multipart, uses test_schema, references myworkflow)
        // ---------------------------------------------------------------------
        (await UploadAsync(client,
            record: """{"resource_type":"ticket","subpath":"/myfolder","shortname":"an_example","attributes":{"workflow_shortname":"myworkflow","is_active":true,"payload":{"schema_shortname":"test_schema","content_type":"json","body":"ticketbody.json"}}}""",
            payloadBytes: Utf8("""{"name":"story","price":22}"""),
            payloadFileName: "ticketbody.json",
            payloadMime: "application/json"))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 13. Lock ticket
        // ---------------------------------------------------------------------
        {
            var resp = await client.PutAsync("/managed/lock/ticket/itest_scenario/myfolder/an_example", content: null);
            (await ReadResponseAsync(resp)).Status.ShouldBe(Status.Success);
        }

        // ---------------------------------------------------------------------
        // 14. Unlock ticket
        // ---------------------------------------------------------------------
        {
            var resp = await client.DeleteAsync("/managed/lock/itest_scenario/myfolder/an_example");
            (await ReadResponseAsync(resp)).Status.ShouldBe(Status.Success);
        }

        // ---------------------------------------------------------------------
        // 15. Create inline content (no schema, nested JSON body)
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request",
            """{"space_name":"itest_scenario","request_type":"create","records":[{"resource_type":"content","subpath":"posts","shortname":"97326c47","attributes":{"payload":{"content_type":"json","body":{"message":"hello from curl.sh"}},"tags":["one","two"]}}]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 16. Create content (multipart, uses test_schema)
        // ---------------------------------------------------------------------
        (await UploadAsync(client,
            record: """{"resource_type":"content","subpath":"myfolder","shortname":"buyer_123","attributes":{"payload":{"content_type":"json","schema_shortname":"test_schema"},"tags":["fun","personal"]}}""",
            payloadBytes: Utf8("""{"name":"Eggs","price":34.99}"""),
            payloadFileName: "data.json",
            payloadMime: "application/json"))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 17. Add comment to content (attachment-flavor resource)
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request",
            """{"space_name":"itest_scenario","request_type":"create","records":[{"resource_type":"comment","subpath":"posts/97326c47","shortname":"greatcomment","attributes":{"body":"A comment inside the content resource"}}]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 18. Managed CSV export — returns text/csv; curl.sh asserts >= 2 lines.
        // ---------------------------------------------------------------------
        {
            var resp = await client.PostAsync("/managed/csv",
                Json("""{"space_name":"itest_scenario","subpath":"myfolder","type":"subpath","filter_schema_names":[],"retrieve_json_payload":true,"limit":5}"""));
            resp.IsSuccessStatusCode.ShouldBeTrue();
            var csv = await resp.Content.ReadAsStringAsync();
            // curl.sh counts `wc -l` and requires ≥ 2 — a header line plus at least one data row.
            csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBeGreaterThanOrEqualTo(2);
        }

        // ---------------------------------------------------------------------
        // 19. Update content (raw request body)
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request",
            """{"space_name":"itest_scenario","request_type":"update","records":[{"resource_type":"content","subpath":"myfolder","shortname":"buyer_123","attributes":{"tags":["fun_UPDATED","personal_UPDATED"],"displayname":{"en":"Updated content"}}}]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 20. Upload media attachment (multipart, binary PNG)
        // ---------------------------------------------------------------------
        (await UploadAsync(client,
            record: """{"resource_type":"media","subpath":"myfolder/buyer_123","shortname":"receipt","attributes":{"tags":["fun","personal"],"is_active":true}}""",
            payloadBytes: TinyPng(),
            payloadFileName: "logo.jpeg",
            payloadMime: "image/jpeg"))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 21. Delete content
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request",
            """{"space_name":"itest_scenario","request_type":"delete","records":[{"resource_type":"content","subpath":"myfolder","shortname":"buyer_123","attributes":{}}]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 22. Query content under /posts
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/query",
            """{"space_name":"itest_scenario","type":"subpath","subpath":"posts","filter_schema_names":[]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 23. Reload security data
        // ---------------------------------------------------------------------
        (await GetResponse(client, "/managed/reload-security-data")).Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 24. Create user
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request",
            """{"space_name":"management","request_type":"create","records":[{"resource_type":"user","subpath":"users","shortname":"distributor","attributes":{"roles":["test_role"],"msisdn":"7895412658","email":"distributor@example.local","password":"Hunter22hunter","is_active":true}}]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 25. Verify user email/msisdn
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request",
            """{"space_name":"management","request_type":"update","records":[{"resource_type":"user","subpath":"users","shortname":"distributor","attributes":{"is_email_verified":true,"is_msisdn_verified":true}}]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 26. Reset user (admin /user/reset)
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/user/reset",
            """{"shortname":"distributor"}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 27. Delete user
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request",
            """{"space_name":"management","request_type":"delete","records":[{"resource_type":"user","subpath":"users","shortname":"distributor","attributes":{}}]}"""))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 28. Cleanup test space
        // ---------------------------------------------------------------------
        (await PostJsonAsResponse(client, "/managed/request", DeleteSpaceBody))
            .Status.ShouldBe(Status.Success);

        // ---------------------------------------------------------------------
        // 29. Server manifest
        // ---------------------------------------------------------------------
        (await GetResponse(client, "/info/manifest")).Status.ShouldBe(Status.Success);
    }

    // ---- helpers ----------------------------------------------------------

    private const string DeleteSpaceBody =
        """{"space_name":"itest_scenario","request_type":"delete","records":[{"resource_type":"space","subpath":"/","shortname":"itest_scenario","attributes":{}}]}""";

    private static StringContent Json(string body)
        => new(body, Encoding.UTF8, "application/json");

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // Same 1×1 PNG that curl.sh uploads.
    private static byte[] TinyPng() => new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    };

    private static async Task<Response> ReadResponseAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body.ShouldNotBeNull();
        return body!;
    }

    private static async Task<Response> GetResponse(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        return await ReadResponseAsync(resp);
    }

    private static async Task PostJson(HttpClient client, string url, string body)
    {
        // Fire-and-forget variant used for the initial cleanup step. Failures here are
        // expected on a fresh DB where the space doesn't exist yet.
        using var _ = await client.PostAsync(url, Json(body));
    }

    private static async Task<Response> PostJsonAsResponse(HttpClient client, string url, string body)
    {
        var resp = await client.PostAsync(url, Json(body));
        return await ReadResponseAsync(resp);
    }

    private static async Task<Response> UploadAsync(
        HttpClient client, string record, byte[] payloadBytes, string payloadFileName, string payloadMime)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("itest_scenario"), "space_name");

        var recordBytes = Encoding.UTF8.GetBytes(record);
        var recordPart = new ByteArrayContent(recordBytes);
        recordPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(recordPart, "request_record", "request_record.json");

        var payloadPart = new ByteArrayContent(payloadBytes);
        payloadPart.Headers.ContentType = new MediaTypeHeaderValue(payloadMime);
        form.Add(payloadPart, "payload_file", payloadFileName);

        var resp = await client.PostAsync("/managed/resource_with_payload", form);
        return await ReadResponseAsync(resp);
    }
}
