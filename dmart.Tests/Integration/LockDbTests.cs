using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

public class LockDbTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public LockDbTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Lock_Then_Unlock_Round_Trip()
    {
        var client = _factory.CreateClient();
        var token = await GetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var space = "test";
        var subpath = "lock-test";
        var shortname = $"lock-{Guid.NewGuid():N}".Substring(0, 12);

        var lockResp = await client.PutAsync($"/managed/lock/content/{space}/{subpath}/{shortname}", null);
        lockResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Trying to lock again as the same user is also a no-op success since the row exists.
        var secondLock = await client.PutAsync($"/managed/lock/content/{space}/{subpath}/{shortname}", null);
        var secondBody = await secondLock.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        // It returns either Success (HTTP 200) or Failed("locked", HTTP 423) depending on race;
        // both are acceptable. The contract is: no exception, no 5xx.
        if (secondBody?.Status == Dmart.Models.Api.Status.Success)
            secondLock.StatusCode.ShouldBe(HttpStatusCode.OK);
        else
            secondLock.StatusCode.ShouldBe((HttpStatusCode)423);

        var unlockResp = await client.DeleteAsync($"/managed/lock/{space}/{subpath}/{shortname}");
        unlockResp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [FactIfPg]
    public async Task Lock_Response_Includes_LockPeriod()
    {
        // /managed/lock must echo the configured lock_period so clients can
        // schedule a refresh before the lock auto-expires. Mirrors Python.
        var client = _factory.CreateClient();
        var token = await GetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var space = "test";
        var subpath = "lock-period-test";
        var shortname = $"lockp-{Guid.NewGuid():N}".Substring(0, 14);

        try
        {
            var lockResp = await client.PutAsync($"/managed/lock/content/{space}/{subpath}/{shortname}", null);
            lockResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await lockResp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);
            body.Attributes.ShouldNotBeNull();
            body.Attributes!.ShouldContainKey("lock_period");
            // Default is 300 seconds — the value must be an integer > 0.
            var periodObj = body.Attributes["lock_period"];
            var period = periodObj is JsonElement je ? je.GetInt32() : Convert.ToInt32(periodObj);
            period.ShouldBeGreaterThan(0);
        }
        finally
        {
            await client.DeleteAsync($"/managed/lock/{space}/{subpath}/{shortname}");
        }
    }

    [FactIfPg]
    public async Task Lock_Expires_After_Very_Short_Period()
    {
        // With LockPeriod overridden to 1 second, a lock acquired by user A
        // is treated as absent 2 seconds later, so user B (or A again) can
        // take it. Exercises the inline TTL check in LockRepository.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
        {
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.LockPeriod = 1);
        }));
        var client = factory.CreateClient();
        var token = await GetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var space = "test";
        var subpath = "lock-expiry-test";
        var shortname = $"locke-{Guid.NewGuid():N}".Substring(0, 14);

        var first = await client.PutAsync($"/managed/lock/content/{space}/{subpath}/{shortname}", null);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Lock's INSERT uses ON CONFLICT DO NOTHING, so a second PUT returns
        // non-OK until the 1s TTL elapses and the purge step deletes the row.
        // Poll the PUT itself — whichever call first lands after expiry wins.
        HttpResponseMessage? refreshed = null;
        var ok = await WaitFor.UntilAsync(async () =>
        {
            refreshed?.Dispose();
            refreshed = await client.PutAsync($"/managed/lock/content/{space}/{subpath}/{shortname}", null);
            return refreshed.StatusCode == HttpStatusCode.OK;
        }, timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200));
        ok.ShouldBeTrue("lock should become re-acquirable after TTL expiry");
        refreshed!.StatusCode.ShouldBe(HttpStatusCode.OK);

        await client.DeleteAsync($"/managed/lock/{space}/{subpath}/{shortname}");
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
