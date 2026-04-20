using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Mirrors dmart's pytests/service_test.py CRUD-via-API checks. Requires DB.
public class ManagedCrudDbTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedCrudDbTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Create_Get_Update_Delete_Roundtrip()
    {
        var client = _factory.CreateClient();
        var token = await GetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var shortname = $"itest-{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        // Create
        var createReq = new Request
        {
            RequestType = RequestType.Create,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                    Attributes = new()
                    {
                        ["displayname"] = "Integration test entry",
                        ["tags"] = new List<string> { "test", "integration" },
                    },
                },
            },
        };
        var createResp = await client.PostAsJsonAsync("/managed/request", createReq, DmartJsonContext.Default.Request);
        if (createResp.StatusCode != HttpStatusCode.OK)
        {
            var body = await createResp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Create failed: {createResp.StatusCode}\n{body}");
        }

        // Get
        var getResp = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{shortname}");
        getResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Update
        var updateReq = new Request
        {
            RequestType = RequestType.Update,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                    Attributes = new() { ["displayname"] = "Updated" },
                },
            },
        };
        var updateResp = await client.PostAsJsonAsync("/managed/request", updateReq, DmartJsonContext.Default.Request);
        updateResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Delete
        var deleteReq = new Request
        {
            RequestType = RequestType.Delete,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                },
            },
        };
        var deleteResp = await client.PostAsJsonAsync("/managed/request", deleteReq, DmartJsonContext.Default.Request);
        deleteResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify gone
        var afterDelete = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{shortname}");
        afterDelete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [FactIfPg]
    public async Task Query_Returns_Success_With_Records_List()
    {
        var client = _factory.CreateClient();
        var token = await GetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var query = new Query
        {
            Type = QueryType.Search,
            SpaceName = "test",
            Subpath = "/",
            Limit = 5,
        };
        var resp = await client.PostAsJsonAsync("/managed/query", query, DmartJsonContext.Default.Query);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
    }

    [FactIfPg]
    public async Task Managed_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/managed/entry/content/test/itest/anything");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Should return a JSON body matching Python's shape:
        // {"status":"failed","error":{"type":"jwtauth","code":49,"message":"..."}}
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldNotBeNullOrWhiteSpace();
        var body = System.Text.Json.JsonSerializer.Deserialize(json, DmartJsonContext.Default.Response);
        body.ShouldNotBeNull();
        body!.Status.ShouldBe(Status.Failed);
        body.Error.ShouldNotBeNull();
        body.Error!.Type.ShouldBe("jwtauth");
    }

    private async Task<string> GetTokenAsync(HttpClient client)
    {
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        return body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"Login failed for '{_factory.AdminShortname}': {resp.StatusCode} {raw}");
    }
}
