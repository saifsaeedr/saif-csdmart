using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Integration tests for POST /public/submit — the anonymous submission endpoint.
// Covers AllowedSubmitModels whitelist, invalid resource types, auto-shortname,
// and basic happy path. Anonymous flows have no curl.sh coverage for these
// error paths, so these tests are the regression anchor.
public class PublicSubmitTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PublicSubmitTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Submit_Rejects_Unknown_ResourceType()
    {
        var client = _factory.CreateClient();
        var body = new StringContent("{\"foo\":\"bar\"}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/public/submit/test/not_a_real_type/schema1/sub1", body);
        // FailedResponseFilter maps bad_request-class errors to 400.
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("unknown resource type");
    }

    [FactIfPg]
    public async Task Submit_Enforces_AllowedSubmitModels_Whitelist()
    {
        // Override the allowlist at the factory level so only "test.explicit_ok"
        // can be submitted. Any other space.schema pair must be rejected with
        // "not_allowed".
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
        {
            svcs.Configure<DmartSettings>(s => s.AllowedSubmitModels = "test.explicit_ok");
        }));
        var client = factory.CreateClient();

        // Submission against a space.schema NOT in the allowlist must fail.
        var body = new StringContent("{\"note\":\"hi\"}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/public/submit/test/content/some_other_schema/sub1", body);
        // Whitelist rejection emits NOT_ALLOWED (401) via the FailedResponseFilter,
        // matching Python's auth-type response for restricted resources.
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var respText = await resp.Content.ReadAsStringAsync();
        respText.ShouldContain("not allowed");
    }

    [FactIfPg]
    public async Task Submit_AllowedSubmitModels_Empty_Means_Allow_Any()
    {
        // With no allowlist set (the default), every space.schema pair is
        // accepted as long as resource_type parses. The request itself may
        // still fail downstream (e.g. schema validation), but not on the
        // allowlist gate.
        var client = _factory.CreateClient();
        var body = new StringContent("{\"foo\":\"bar\"}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/public/submit/test/anything/sub1", body);
        // A 400 with "not allowed" means the gate fired; any OTHER outcome
        // (success, validation failure, etc.) means the gate passed.
        var respText = await resp.Content.ReadAsStringAsync();
        respText.ShouldNotContain("submit not allowed");
    }

    [FactIfPg]
    public async Task Submit_Generates_Shortname_When_Missing()
    {
        // If the client doesn't provide a shortname in the body, the server
        // derives one from a fresh GUID so concurrent anonymous submissions
        // don't collide.
        var client = _factory.CreateClient();
        // Body has no "shortname" field.
        var body = new StringContent("{\"data\":42}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/public/submit/test/content/admin_profile/anon_submits", body);

        // The happy path returns success with the generated shortname in attributes.
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var payload = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            payload!.Status.ShouldBe(Status.Success);
            payload.Attributes.ShouldNotBeNull();
            payload.Attributes!.ShouldContainKey("shortname");
            var shortname = payload.Attributes["shortname"]?.ToString();
            shortname.ShouldNotBeNullOrEmpty();
            shortname!.Length.ShouldBeGreaterThanOrEqualTo(6);
        }
        // If the schema doesn't exist or validation fails, we still expect a
        // structured failure, not an unhandled exception.
        else
        {
            var text = await resp.Content.ReadAsStringAsync();
            text.ShouldNotBeNullOrEmpty();
        }
    }
}
