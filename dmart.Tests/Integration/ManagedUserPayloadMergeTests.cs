using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Regression for the managed user-update payload bug: a partial attributes.payload.body
// on POST /managed/request (request_type=update) must DEEP-MERGE into the existing body
// — preserving untouched keys — not replace it wholesale. A key sent as null is removed.
public class ManagedUserPayloadMergeTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedUserPayloadMergeTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Partial_Body_Patch_Preserves_Other_Keys_And_Null_Removes()
    {
        var admin = await _factory.CreateLoggedInUserAsync();      // super_admin
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = "pmerge_" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            // Create the user with a two-key payload body.
            (await Send(admin.Client, "create",
                $"\"shortname\":\"{shortname}\",\"attributes\":{{\"is_active\":true," +
                "\"payload\":{\"content_type\":\"json\",\"body\":{\"a\":1,\"b\":2}}}"))
                .ShouldBe(Status.Success);

            // Update with ONLY b — a must survive, b must change.
            (await Send(admin.Client, "update",
                $"\"shortname\":\"{shortname}\",\"attributes\":{{\"payload\":{{\"body\":{{\"b\":99}}}}}}"))
                .ShouldBe(Status.Success);

            var body = (await users.GetByShortnameAsync(shortname))!.Payload!.Body!.Value;
            body.GetProperty("a").GetInt32().ShouldBe(1);   // preserved (the bug deleted this)
            body.GetProperty("b").GetInt32().ShouldBe(99);  // patched

            // Null removes a key (Python remove_none_dict parity).
            (await Send(admin.Client, "update",
                $"\"shortname\":\"{shortname}\",\"attributes\":{{\"payload\":{{\"body\":{{\"a\":null}}}}}}"))
                .ShouldBe(Status.Success);

            var after = (await users.GetByShortnameAsync(shortname))!.Payload!.Body!.Value;
            after.TryGetProperty("a", out _).ShouldBeFalse();   // removed
            after.GetProperty("b").GetInt32().ShouldBe(99);     // still there
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    private static async Task<Status> Send(HttpClient client, string requestType, string record)
    {
        var resp = await client.PostAsync("/managed/request", new StringContent(
            $"{{\"space_name\":\"management\",\"request_type\":\"{requestType}\",\"records\":[{{" +
            $"\"resource_type\":\"user\",\"subpath\":\"users\",{record}}}]}}",
            Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return body!.Status;
    }
}
