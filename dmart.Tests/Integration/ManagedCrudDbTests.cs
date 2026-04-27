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
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

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

    // Python parity: RequestType.update_acl writes record.attributes["acl"]
    // through to the persisted entry; regular RequestType.update keeps acl in
    // Meta.restricted_fields and silently ignores it. The C# port previously
    // dropped acl on both paths because EntryService.ApplyPatch never read
    // the "acl" key. This test pins both halves of the parity.
    // Python parity: User update (`request_type=update` for `resource_type=user`)
    // must persist the `payload` block from `attributes`. The C# port previously
    // dropped it because the User branch of DispatchUpdateAsync didn't read
    // attrs["payload"] at all — clients posting `attributes.payload` got 200 OK
    // but the field never landed in the users.payload jsonb column.
    [FactIfPg]
    public async Task User_Update_Persists_Payload_Block()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        var shortname = $"u{Guid.NewGuid():N}".Substring(0, 12);
        var managementSpace = "management";

        // Create a user with no payload.
        var createReq = new Request
        {
            RequestType = RequestType.Create,
            SpaceName = managementSpace,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.User,
                    Subpath = "users",
                    Shortname = shortname,
                    Attributes = new()
                    {
                        ["password"] = "Pa55word!",
                        ["roles"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                        ["msisdn"] = "9645" + Random.Shared.Next(1_000_000, 9_999_999).ToString(),
                    },
                },
            },
        };
        (await client.PostAsJsonAsync("/managed/request", createReq, DmartJsonContext.Default.Request))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        try
        {
            // Update with a payload block — exact shape the user reported.
            var updateReq = new Request
            {
                RequestType = RequestType.Update,
                SpaceName = managementSpace,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.User,
                        Subpath = "users",
                        Shortname = shortname,
                        Attributes = new()
                        {
                            ["payload"] = JsonSerializer.SerializeToElement(new
                            {
                                content_type = "json",
                                body = new
                                {
                                    legacy = new
                                    {
                                        created_at = "2018-10-07T10:42:57",
                                    },
                                },
                            }),
                        },
                    },
                },
            };
            (await client.PostAsJsonAsync("/managed/request", updateReq, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // Fetch and confirm the payload landed.
            var getResp = await client.GetAsync($"/managed/entry/user/{managementSpace}/users/{shortname}?retrieve_json_payload=true");
            getResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await getResp.Content.ReadAsStringAsync();
            body.ShouldContain("\"payload\":");
            body.ShouldContain("\"legacy\"");
            body.ShouldContain("2018-10-07T10:42:57");
        }
        finally
        {
            var cleanup = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = managementSpace,
                Records = new() { new Record { ResourceType = ResourceType.User, Subpath = "users", Shortname = shortname } },
            };
            await client.PostAsJsonAsync("/managed/request", cleanup, DmartJsonContext.Default.Request);
        }
    }

    [FactIfPg]
    public async Task UpdateAcl_Writes_Acl_RegularUpdate_Ignores_It()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        var shortname = $"acltest-{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

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
                    Attributes = new() { ["displayname"] = "ACL parity probe" },
                },
            },
        };
        (await client.PostAsJsonAsync("/managed/request", createReq, DmartJsonContext.Default.Request))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        try
        {
            // 1. Regular update with an acl attribute → must NOT persist (Python's
            //    update_from_record skips restricted_fields).
            var sneakyAcl = new Request
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
                        Attributes = new()
                        {
                            ["displayname"] = "still updating",
                            ["acl"] = JsonSerializer.SerializeToElement(new[]
                            {
                                new { user_shortname = "intruder", allowed_actions = new[] { "view", "update" } },
                            }),
                        },
                    },
                },
            };
            (await client.PostAsJsonAsync("/managed/request", sneakyAcl, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var afterRegular = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{shortname}");
            afterRegular.StatusCode.ShouldBe(HttpStatusCode.OK);
            var regularBody = await afterRegular.Content.ReadAsStringAsync();
            // Python restricted_fields parity: regular update must not write acl.
            regularBody.ShouldNotContain("\"intruder\"");

            // 2. update_acl request → MUST persist the new acl.
            var setAcl = new Request
            {
                RequestType = RequestType.UpdateAcl,
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
                            ["acl"] = JsonSerializer.SerializeToElement(new[]
                            {
                                new { user_shortname = "alice", allowed_actions = new[] { "view" } },
                            }),
                        },
                    },
                },
            };
            (await client.PostAsJsonAsync("/managed/request", setAcl, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var afterUpdateAcl = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{shortname}");
            afterUpdateAcl.StatusCode.ShouldBe(HttpStatusCode.OK);
            var aclBody = await afterUpdateAcl.Content.ReadAsStringAsync();
            // update_acl must persist record.attributes["acl"] onto the entry.
            aclBody.ShouldContain("\"alice\"");
        }
        finally
        {
            var cleanup = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = space,
                Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = subpath, Shortname = shortname } },
            };
            await client.PostAsJsonAsync("/managed/request", cleanup, DmartJsonContext.Default.Request);
        }
    }

    [FactIfPg]
    public async Task Query_Returns_Success_With_Records_List()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

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

}
