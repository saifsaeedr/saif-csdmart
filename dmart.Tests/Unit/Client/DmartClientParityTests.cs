using System.Net;
using System.Text;
using System.Text.Json;
using Dmart.Client;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Client;

// Pins the wire shape of dmart.Client's typed parity facade
// (DmartClient.Parity.cs + DmartClient.Endpoints.cs) and the auth-adjacent
// social mobile-login wrappers. Same mocked-HttpMessageHandler pattern as
// DmartClientTests so we never touch the network.
public class DmartClientParityTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Responder { get; set; } =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"status\":\"success\"}", Encoding.UTF8, "application/json") });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await Responder(request);
        }
    }

    private static (DmartClient client, RecordingHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? responder = null)
    {
        var handler = new RecordingHandler();
        if (responder is not null) handler.Responder = responder;
        var http = new HttpClient(handler);
        var client = new DmartClient("https://dmart.test", http);
        return (client, handler);
    }

    // ============================================================
    // Typed CRUD facade — DmartClient.Parity.cs
    // ============================================================

    [Fact]
    public async Task LoadAsync_Hits_Managed_Entry_Endpoint_And_Parses_Entry()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"uuid":"00000000-0000-0000-0000-000000000001","shortname":"t1","space_name":"app","subpath":"/","resource_type":"ticket","is_active":true,"owner_shortname":"alice","state":"new","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}""")));

        var entry = await client.LoadAsync("app", "/", "t1", ResourceType.Ticket);

        entry.ShouldNotBeNull();
        entry!.Shortname.ShouldBe("t1");
        entry.State.ShouldBe("new");
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldStartWith("/managed/entry/ticket/app/");
    }

    [Fact]
    public async Task LoadAsync_Returns_Null_On_404()
    {
        var (client, _) = Make(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"status":"failed","error":{"type":"db","code":11,"message":"not found"}}""",
                Encoding.UTF8, "application/json"),
        }));

        var entry = await client.LoadAsync("app", "/", "missing", ResourceType.Ticket);
        entry.ShouldBeNull();
    }

    [Fact]
    public async Task CreateAsync_Sends_RequestType_Create()
    {
        var (client, handler) = Make();

        var entry = new Entry
        {
            Uuid = "00000000-0000-0000-0000-000000000002",
            Shortname = "t2",
            SpaceName = "app",
            Subpath = "/",
            ResourceType = ResourceType.Ticket,
            IsActive = true,
            OwnerShortname = "alice",
            State = "new",
        };

        await client.CreateAsync(entry);

        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/request");
        handler.LastBody.ShouldNotBeNull();
        handler.LastBody!.ShouldContain("\"request_type\":\"create\"");
        handler.LastBody!.ShouldContain("\"shortname\":\"t2\"");
    }

    [Fact]
    public async Task DeleteAsync_Sends_RequestType_Delete_And_Returns_Bool()
    {
        var (client, handler) = Make();

        var ok = await client.DeleteAsync(new Locator(ResourceType.Ticket, "app", "/", "t1"));

        ok.ShouldBeTrue();
        handler.LastBody!.ShouldContain("\"request_type\":\"delete\"");
        handler.LastBody!.ShouldContain("\"shortname\":\"t1\"");
    }

    [Fact]
    public async Task MoveAsync_Embeds_Dest_Attributes()
    {
        var (client, handler) = Make();

        await client.MoveAsync(
            new Locator(ResourceType.Content, "app", "/a", "doc1"),
            new Locator(ResourceType.Content, "app", "/b", "doc1"));

        handler.LastBody!.ShouldContain("\"request_type\":\"move\"");
        handler.LastBody!.ShouldContain("\"dest_subpath\":\"/b\"");
    }

    [Fact]
    public async Task MoveAsync_Across_Spaces_Throws()
    {
        var (client, _) = Make();
        await Should.ThrowAsync<ArgumentException>(() => client.MoveAsync(
            new Locator(ResourceType.Content, "app", "/a", "doc1"),
            new Locator(ResourceType.Content, "other", "/a", "doc1")));
    }

    // ============================================================
    // Server-endpoint wrappers — DmartClient.Endpoints.cs
    // ============================================================

    [Fact]
    public async Task LockEntryAsync_Builds_Correct_Path()
    {
        var (client, handler) = Make();

        await client.LockEntryAsync(ResourceType.Ticket, "app", "/tickets", "t1");

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Put);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/managed/lock/ticket/app/tickets/t1");
    }

    [Fact]
    public async Task UnlockEntryAsync_Uses_Delete()
    {
        var (client, handler) = Make();
        await client.UnlockEntryAsync("app", "/tickets", "t1");
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/managed/lock/app/tickets/t1");
    }

    [Fact]
    public async Task DeleteAccountAsync_Clears_Token_Regardless_Of_Outcome()
    {
        var (client, _) = Make();
        client.AuthToken = "before-delete";

        await client.DeleteAccountAsync();
        client.AuthToken.ShouldBeNull();
    }

    [Fact]
    public async Task GetInfoMeAsync_Hits_Info_Me()
    {
        var (client, handler) = Make();
        await client.GetInfoMeAsync();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/info/me");
    }

    [Fact]
    public async Task AppleMobileLoginAsync_Posts_Token_And_Stores_Bearer()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"status":"success","records":[{"resource_type":"user","shortname":"u","subpath":"users","attributes":{"access_token":"apple-token","type":"mobile","roles":[]}}]}""")));

        var resp = await client.AppleMobileLoginAsync("eyJ-fake-id-token");

        resp.Status.ShouldBe(Status.Success);
        client.AuthToken.ShouldBe("apple-token");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/user/apple/mobile-login");
        handler.LastBody!.ShouldContain("\"token\":\"eyJ-fake-id-token\"");
    }

    [Fact]
    public async Task ReindexEmbeddingsAsync_Sends_Space_Name_Body()
    {
        var (client, handler) = Make();
        await client.ReindexEmbeddingsAsync("app");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/reindex-embeddings");
        handler.LastBody!.ShouldContain("\"space_name\":\"app\"");
    }

    [Fact]
    public async Task ApplyAlterationAsync_Embeds_Both_Path_Segments()
    {
        var (client, handler) = Make();
        await client.ApplyAlterationAsync("app", "v2-rename");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/apply-alteration/app/v2-rename");
    }

    [Fact]
    public async Task PublicQueryGetAsync_Builds_Type_And_Subpath_Segments()
    {
        var (client, handler) = Make();
        await client.PublicQueryGetAsync(QueryType.Search, "app", "/news");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/public/query/search/app/news");
    }

    [Fact]
    public async Task PublicExecuteAsync_Preserves_Server_Excute_Typo_In_Path()
    {
        // The server's route is literally /public/excute (typo). The wrapper
        // must keep that spelling so calls land on the registered endpoint —
        // if the server's path ever changes, this test fails loudly.
        var (client, handler) = Make();
        await client.PublicExecuteAsync("notify", "app", new Dictionary<string, object?>
        {
            ["payload"] = "ping",
        });
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/public/excute/notify/app");
    }

    // ============================================================
    // Alternative-identifier login — DmartClient.cs LoginByAsync
    // ============================================================

    [Fact]
    public async Task LoginByAsync_Posts_Credentials_Merged_With_Password_And_Stores_Token()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"status":"success","records":[{"resource_type":"user","shortname":"u","subpath":"users","attributes":{"access_token":"loginby-token","type":"web","roles":[]}}]}""")));

        var resp = await client.LoginByAsync(new Dictionary<string, string>
        {
            ["email"] = "john@example.com",
        }, "s3cret");

        resp.Status.ShouldBe(Status.Success);
        client.AuthToken.ShouldBe("loginby-token");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/user/login");
        handler.LastBody!.ShouldContain("\"email\":\"john@example.com\"");
        handler.LastBody!.ShouldContain("\"password\":\"s3cret\"");
    }

    // ============================================================
    // UUID / slug lookups — DmartClient.Parity.cs additions
    // ============================================================

    [Fact]
    public async Task GetByUuidAsync_Hits_Managed_Byuuid_And_Parses_Entry()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"uuid":"11111111-2222-3333-4444-555555555555","shortname":"t9","space_name":"app","subpath":"/","resource_type":"content","is_active":true,"owner_shortname":"alice","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}""")));

        var entry = await client.GetByUuidAsync(new Guid("11111111-2222-3333-4444-555555555555"));

        entry.ShouldNotBeNull();
        entry!.Shortname.ShouldBe("t9");
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/managed/byuuid/11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public async Task GetByUuidAsync_Returns_Null_On_404()
    {
        var (client, _) = Make(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"status":"failed","error":{"type":"db","code":11,"message":"not found"}}""",
                Encoding.UTF8, "application/json"),
        }));

        var entry = await client.GetByUuidAsync(Guid.NewGuid());
        entry.ShouldBeNull();
    }

    [Fact]
    public async Task GetBySlugAsync_Hits_Managed_Byslug_And_Escapes_Slug()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"uuid":"11111111-2222-3333-4444-555555555555","shortname":"t9","space_name":"app","subpath":"/","resource_type":"content","is_active":true,"owner_shortname":"alice","slug":"my slug","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}""")));

        var entry = await client.GetBySlugAsync("my slug");

        entry.ShouldNotBeNull();
        // Uri.EscapeDataString turns the space into %20, not '+'.
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/byslug/my%20slug");
    }

    [Fact]
    public async Task GetEntryByCriteriaAsync_Uuid_Routes_To_Byuuid()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"uuid":"11111111-2222-3333-4444-555555555555","shortname":"t9","space_name":"app","subpath":"/","resource_type":"content","is_active":true,"owner_shortname":"alice","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}""")));

        var entry = await client.GetEntryByCriteriaAsync(new Dictionary<string, object?>
        {
            ["uuid"] = "11111111-2222-3333-4444-555555555555",
        });

        entry.ShouldNotBeNull();
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldStartWith("/managed/byuuid/");
    }

    [Fact]
    public async Task GetEntryByCriteriaAsync_Slug_Routes_To_Byslug()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"uuid":"11111111-2222-3333-4444-555555555555","shortname":"t9","space_name":"app","subpath":"/","resource_type":"content","is_active":true,"owner_shortname":"alice","slug":"foo","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}""")));

        await client.GetEntryByCriteriaAsync(new Dictionary<string, object?> { ["slug"] = "foo" });

        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/byslug/foo");
    }

    [Fact]
    public async Task GetEntryByCriteriaAsync_Falls_Back_To_Query_For_Shortname()
    {
        // Server's query response carries space_name inside Record.Attributes;
        // DeserializeEntryFromRecord then merges attributes into the Entry shape.
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"status":"success","records":[{"resource_type":"content","shortname":"t1","subpath":"/","uuid":"11111111-2222-3333-4444-555555555555","attributes":{"space_name":"app","is_active":true,"tags":[],"owner_shortname":"alice","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}}]}""")));

        var entry = await client.GetEntryByCriteriaAsync(new Dictionary<string, object?>
        {
            ["space_name"] = "app",
            ["subpath"] = "/",
            ["shortname"] = "t1",
        });

        entry.ShouldNotBeNull();
        entry!.Shortname.ShouldBe("t1");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/query");
        handler.LastBody!.ShouldContain("\"filter_shortnames\":[\"t1\"]");
    }

    // ============================================================
    // Schema, history, and lock parity wrappers
    // ============================================================

    [Fact]
    public async Task GetSchemaAsync_Loads_Schema_Resource_Type()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"uuid":"11111111-2222-3333-4444-555555555555","shortname":"my_schema","space_name":"app","subpath":"/schema","resource_type":"schema","is_active":true,"owner_shortname":"alice","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}""")));

        var entry = await client.GetSchemaAsync("app", "my_schema");

        entry.ShouldNotBeNull();
        entry!.ResourceType.ShouldBe(ResourceType.Schema);
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/entry/schema/app/schema/my_schema");
    }

    [Fact]
    public async Task QueryHistoryAsync_Calls_Query_With_History_Type_And_Maps_Rows()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"status":"success","records":[{"resource_type":"content","shortname":"t1","subpath":"/","uuid":"11111111-2222-3333-4444-555555555555","attributes":{"timestamp":"2026-01-15T12:00:00Z","owner_shortname":"alice","request_headers":{"x-request-id":"r1"},"diff":{"state":["new","open"]}}}]}""")));

        var rows = await client.QueryHistoryAsync("app", "/tickets", "t1", limit: 10);

        rows.Count.ShouldBe(1);
        rows[0].Uuid.ShouldBe("11111111-2222-3333-4444-555555555555");
        rows[0].Shortname.ShouldBe("t1");
        rows[0].OwnerShortname.ShouldBe("alice");
        rows[0].Diff.ShouldNotBeNull();
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/query");
        handler.LastBody!.ShouldContain("\"type\":\"history\"");
        handler.LastBody!.ShouldContain("\"filter_shortnames\":[\"t1\"]");
    }

    [Fact]
    public async Task TryLockAsync_Returns_True_On_Success()
    {
        var (client, handler) = Make();

        var ok = await client.TryLockAsync(
            new Locator(ResourceType.Ticket, "app", "/tickets", "t1"), "alice");

        ok.ShouldBeTrue();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Put);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/managed/lock/ticket/app/tickets/t1");
    }

    [Fact]
    public async Task TryLockAsync_Returns_False_On_423()
    {
        var (client, _) = Make(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)423)
        {
            Content = new StringContent(
                """{"status":"failed","error":{"type":"db","code":423,"message":"locked"}}""",
                Encoding.UTF8, "application/json"),
        }));

        var ok = await client.TryLockAsync(
            new Locator(ResourceType.Ticket, "app", "/tickets", "t1"), "alice");

        ok.ShouldBeFalse();
    }

    [Fact]
    public async Task UnlockAsync_Sends_Delete_And_Returns_Bool()
    {
        var (client, handler) = Make();

        var ok = await client.UnlockAsync(
            new Locator(ResourceType.Ticket, "app", "/tickets", "t1"), "alice");

        ok.ShouldBeTrue();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/managed/lock/app/tickets/t1");
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
