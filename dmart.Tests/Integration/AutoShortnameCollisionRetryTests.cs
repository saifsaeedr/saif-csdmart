using System.Net.Http.Json;
using System.Text;
using Dmart.Api.Managed;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end proof that an auto shortname survives a REAL database collision: the
// UUID source is scripted so the first mint lands on an already-existing shortname
// in the same folder, and the create must transparently retry and succeed with the
// second mint — all while keeping the 8-char prefix (Python parity), so collisions
// are solved by retry, not by a longer name.
public class AutoShortnameCollisionRetryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    private const string TestSpace = "autoretrytest";
    public AutoShortnameCollisionRetryTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Auto_Create_Retries_Past_A_Shortname_Collision()
    {
        var u = await _factory.CreateLoggedInUserAsync();
        var client = u.Client;

        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var taken = g1.ToString("N")[..8];   // first mint collides with this
        var free = g2.ToString("N")[..8];    // second mint is free
        taken.ShouldNotBe(free);

        // Script the UUID source: first auto resolve → g1 (collides), second → g2.
        var queue = new Queue<Guid>(new[] { g1, g2 });
        RequestHandler.NewAutoUuid = () => queue.Dequeue();
        try
        {
            await client.PostAsync("/managed/request", Body(
                $"{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{TestSpace}\",\"attributes\":{{\"is_active\":true}}}}"));
            await client.PostAsync("/managed/request", Body(
                "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"f\",\"attributes\":{\"is_active\":true}}"));

            // Seed an entry occupying `taken` in folder /f so the first auto mint collides.
            var seed = await client.PostAsync("/managed/request", Body(
                $"{{\"resource_type\":\"content\",\"subpath\":\"f\",\"shortname\":\"{taken}\",\"attributes\":{{\"is_active\":true,\"payload\":{{\"content_type\":\"json\",\"body\":{{\"seed\":true}}}}}}}}"));
            (await seed.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);

            // Create with shortname "auto": mint #1 (g1) collides with `taken`, the
            // retry mints #2 (g2) which is free → success with `free`.
            var resp = await client.PostAsync("/managed/request", Body(
                "{\"resource_type\":\"content\",\"subpath\":\"f\",\"shortname\":\"auto\",\"attributes\":{\"is_active\":true,\"payload\":{\"content_type\":\"json\",\"body\":{\"v\":1}}}}"));
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);

            body!.Status.ShouldBe(Status.Success);                 // collision did NOT fail the create
            body.Records![0].Shortname.ShouldBe(free);             // retried onto the second mint
            body.Records![0].Shortname.Length.ShouldBe(8);         // still 8 — parity preserved
            queue.Count.ShouldBe(0);                               // both mints consumed → it retried exactly once
        }
        finally
        {
            RequestHandler.NewAutoUuid = Guid.NewGuid;
            await client.PostAsync("/managed/request", new StringContent(
                $"{{\"space_name\":\"{TestSpace}\",\"request_type\":\"delete\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{TestSpace}\",\"attributes\":{{}}}}]}}",
                Encoding.UTF8, "application/json"));
        }
    }

    private static StringContent Body(string record) =>
        new($"{{\"space_name\":\"{TestSpace}\",\"request_type\":\"create\",\"records\":[{record}]}}",
            Encoding.UTF8, "application/json");
}
