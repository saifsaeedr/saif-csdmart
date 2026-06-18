using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Liveness/readiness probes for orchestrators (k8s, blue-green). Both must be
// reachable WITHOUT authentication — a probe carries no JWT — and return the
// canonical dmart Response envelope so log/scrape tooling sees a uniform wire
// shape. /health/ready actually pings PostgreSQL so a node with a dead DB
// connection is taken out of rotation.
public sealed class HealthEndpointTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public HealthEndpointTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Live_Returns_200_Without_Auth()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/live");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize(
            await resp.Content.ReadAsStringAsync(),
            Dmart.Models.Json.DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Dmart.Models.Api.Status.Success);
    }

    [FactIfPg]
    public async Task Ready_Returns_200_When_Db_Reachable()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/ready");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize(
            await resp.Content.ReadAsStringAsync(),
            Dmart.Models.Json.DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Dmart.Models.Api.Status.Success);
    }
}
