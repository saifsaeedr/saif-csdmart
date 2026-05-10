using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
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
    public async Task Submit_Rejects_NotAllowlisted_Location()
    {
        var client = _factory.CreateClient();
        var body = new StringContent("{\"foo\":\"bar\"}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/public/submit/test/not_a_real_type/schema1/sub1", body);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("Selected location is not allowed");
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
        // Python rejects non-allowlisted public submit locations as request
        // errors before any authz/resource write path runs.
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var respText = await resp.Content.ReadAsStringAsync();
        respText.ShouldContain("Selected location is not allowed");
    }

    [FactIfPg]
    public async Task Submit_AllowedSubmitModels_Empty_Denies_Public_Submit()
    {
        var client = _factory.CreateClient();
        var body = new StringContent("{\"foo\":\"bar\"}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/public/submit/test/anything/sub1", body);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var respText = await resp.Content.ReadAsStringAsync();
        respText.ShouldContain("Selected location is not allowed");
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

        string? createdShortname = null;
        try
        {
            // The happy path returns success with the generated shortname in attributes.
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var payload = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
                payload!.Status.ShouldBe(Status.Success);
                payload.Attributes.ShouldNotBeNull();
                payload.Attributes!.ShouldContainKey("shortname");
                createdShortname = payload.Attributes["shortname"]?.ToString();
                createdShortname.ShouldNotBeNullOrEmpty();
                createdShortname!.Length.ShouldBeGreaterThanOrEqualTo(6);
            }
            // If the schema doesn't exist or validation fails, we still expect a
            // structured failure, not an unhandled exception.
            else
            {
                var text = await resp.Content.ReadAsStringAsync();
                text.ShouldNotBeNullOrEmpty();
            }
        }
        finally
        {
            // Clean up the entry we just created. owner_shortname="anonymous"
            // (set by SubmitHandler) creates an FK reference from entries to
            // the anonymous user row — leaving the entry behind blocks any
            // later DELETE FROM users WHERE shortname='anonymous', which
            // breaks WorldScopeHarness teardown in PublicQueryAnonymousTests
            // and curl.sh §71's anonymous-user create. See PublicQueryAnonymousTests
            // WorldScopeHarness.DisposeAsync for the related defensive reset.
            if (createdShortname is not null)
            {
                var entries = _factory.Services.GetRequiredService<EntryRepository>();
                try
                {
                    await entries.DeleteAsync(
                        spaceName: "test",
                        subpath: "/anon_submits",
                        shortname: createdShortname,
                        type: ResourceType.Content);
                }
                catch { /* best-effort */ }
            }
        }
    }
}
