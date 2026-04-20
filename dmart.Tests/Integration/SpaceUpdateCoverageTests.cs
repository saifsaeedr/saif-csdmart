using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the /managed/request → Space update path. Commit 11af448 extended
// DispatchUpdateAsync for ResourceType.Space to patch every wire-visible
// field (previously only HideSpace + IsActive flipped through). This test
// seeds a minimal space, PATCHes every field in one request, reads back
// via SpaceRepository, and asserts each round-tripped. Without these
// assertions, a future refactor that drops a field from the update path
// would silently lose it — the way it used to.
public sealed class SpaceUpdateCoverageTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SpaceUpdateCoverageTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Space_Update_Round_Trips_Every_Patched_Field()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var loginResp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await loginResp.Content.ReadAsStringAsync();
        var loginBody = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = loginBody!.Records!.First().Attributes!["access_token"]!.ToString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var spaceName = $"itest_upd_{Guid.NewGuid():N}"[..16];

        try
        {
            // Create — only mandatory scaffolding. Every other field is patched below.
            var createReq = new Request
            {
                RequestType = RequestType.Create,
                SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Space,
                        Subpath = "/",
                        Shortname = spaceName,
                        Attributes = new() { ["is_active"] = true },
                    },
                },
            };
            var createResp = await client.PostAsJsonAsync("/managed/request", createReq, DmartJsonContext.Default.Request);
            createResp.StatusCode.ShouldBe(HttpStatusCode.OK, await createResp.Content.ReadAsStringAsync());

            // Update — every newly-wired attribute in one shot.
            var updateReq = new Request
            {
                RequestType = RequestType.Update,
                SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Space,
                        Subpath = "/",
                        Shortname = spaceName,
                        Attributes = new()
                        {
                            // Metas-base
                            ["slug"] = "custom-slug",
                            ["displayname"] = new Dictionary<string, object> { ["en"] = "Custom Display" },
                            ["description"] = new Dictionary<string, object> { ["en"] = "Custom Description" },
                            ["tags"] = new List<string> { "tag_a", "tag_b" },
                            ["is_active"] = false,
                            // Space-specific
                            ["root_registration_signature"] = "rrs_value",
                            ["primary_website"] = "https://example.com",
                            ["indexing_enabled"] = true,
                            ["capture_misses"] = true,
                            ["check_health"] = true,
                            ["languages"] = new List<string> { "english", "ar" }, // long + short form
                            ["icon"] = "icon_name",
                            ["mirrors"] = new List<string> { "https://mirror1", "https://mirror2" },
                            ["hide_folders"] = new List<string> { "_internal", "scratch" },
                            ["hide_space"] = true,
                            ["active_plugins"] = new List<string> { "plugin_a", "plugin_b" },
                            ["ordinal"] = 7,
                        },
                    },
                },
            };
            var updateResp = await client.PostAsJsonAsync("/managed/request", updateReq, DmartJsonContext.Default.Request);
            updateResp.StatusCode.ShouldBe(HttpStatusCode.OK, await updateResp.Content.ReadAsStringAsync());

            // Round-trip verify — read from the repo, not the wire, so we
            // assert the DB column actually holds each value.
            var saved = await spaces.GetAsync(spaceName);
            saved.ShouldNotBeNull();

            // Metas-base
            saved!.Slug.ShouldBe("custom-slug");
            saved.Displayname!.En.ShouldBe("Custom Display");
            saved.Description!.En.ShouldBe("Custom Description");
            saved.Tags.ShouldBe(new[] { "tag_a", "tag_b" });
            saved.IsActive.ShouldBeFalse();

            // Space-specific
            saved.RootRegistrationSignature.ShouldBe("rrs_value");
            saved.PrimaryWebsite.ShouldBe("https://example.com");
            saved.IndexingEnabled.ShouldBeTrue();
            saved.CaptureMisses.ShouldBeTrue();
            saved.CheckHealth.ShouldBeTrue();
            // "english" → En, "ar" → Ar — both wire forms must resolve.
            saved.Languages.ShouldContain(Language.En);
            saved.Languages.ShouldContain(Language.Ar);
            saved.Icon.ShouldBe("icon_name");
            saved.Mirrors.ShouldNotBeNull();
            saved.Mirrors!.ShouldBe(new[] { "https://mirror1", "https://mirror2" });
            saved.HideFolders.ShouldNotBeNull();
            saved.HideFolders!.ShouldBe(new[] { "_internal", "scratch" });
            saved.HideSpace.ShouldBe(true);
            saved.ActivePlugins.ShouldNotBeNull();
            saved.ActivePlugins!.ShouldBe(new[] { "plugin_a", "plugin_b" });
            saved.Ordinal.ShouldBe(7);
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }

    [FactIfPg]
    public async Task Space_Update_Absent_Fields_Preserve_Existing_Values()
    {
        // Sibling contract: a partial patch that omits a field must NOT clear
        // the stored value. Guards against a regression where a `?? existing`
        // fallback gets replaced with a bare extract that turns absent → null.
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var loginResp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await loginResp.Content.ReadAsStringAsync();
        var loginBody = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = loginBody!.Records!.First().Attributes!["access_token"]!.ToString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var spaceName = $"itest_preserve_{Guid.NewGuid():N}"[..16];

        try
        {
            // Create a bare space (the create-path only patches a subset of
            // fields anyway — separate bug, out of scope for this commit).
            var createReq = new Request
            {
                RequestType = RequestType.Create,
                SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Space,
                        Subpath = "/",
                        Shortname = spaceName,
                        Attributes = new() { ["is_active"] = true },
                    },
                },
            };
            (await client.PostAsJsonAsync("/managed/request", createReq, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // First update: populate icon + primary_website via the extended
            // update path. This is the fixture for the preservation check.
            var populateReq = new Request
            {
                RequestType = RequestType.Update,
                SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Space,
                        Subpath = "/",
                        Shortname = spaceName,
                        Attributes = new()
                        {
                            ["icon"] = "seed_icon",
                            ["primary_website"] = "https://seed.example",
                        },
                    },
                },
            };
            (await client.PostAsJsonAsync("/managed/request", populateReq, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // Second update: ONLY hide_space — icon/primary_website MUST survive.
            var preserveReq = new Request
            {
                RequestType = RequestType.Update,
                SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Space,
                        Subpath = "/",
                        Shortname = spaceName,
                        Attributes = new() { ["hide_space"] = true },
                    },
                },
            };
            (await client.PostAsJsonAsync("/managed/request", preserveReq, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var saved = await spaces.GetAsync(spaceName);
            saved.ShouldNotBeNull();
            saved!.HideSpace.ShouldBe(true);
            saved.Icon.ShouldBe("seed_icon", "omitted field must preserve its stored value");
            saved.PrimaryWebsite.ShouldBe("https://seed.example");
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
        }
    }
}
