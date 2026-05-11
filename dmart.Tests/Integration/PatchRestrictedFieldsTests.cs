using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins three pieces of Python parity in the /managed/request UPDATE flow:
//
//  1. `tags` arriving as a JSON array round-trips. After source-gen
//     deserialization, `Attributes["tags"]` is a JsonElement (not a
//     List<object>), and the prior `is IEnumerable<object>` pattern in
//     EntryService.ApplyPatch silently dropped it. PatchTags now handles
//     JsonElement explicitly.
//
//  2. `is_active` as a JSON bool round-trips, for the same reason as (1):
//     the value lands as a JsonElement of ValueKind.True/False, not a
//     native bool. PatchBool handles JsonElement now.
//
//  3. `owner_shortname` is silently ignored on regular UPDATE — Python
//     lists it in Meta.restricted_fields. Without this gate the new
//     `owner_shortname = EXCLUDED.owner_shortname` clause in
//     EntryRepository.UpsertAsync would let any authenticated caller
//     transfer ownership through their patch body.
public class PatchRestrictedFieldsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PatchRestrictedFieldsTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Patch_Tags_Array_RoundTrips()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"tagstest_{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname,
            attributes: new() { ["displayname"] = "tags probe" });

        try
        {
            var update = BuildUpdate(space, subpath, shortname, new()
            {
                // List<string> serializes to JSON array, deserializes to JsonElement
                // on the server — exactly the path the PatchTags fix exercises.
                ["tags"] = new List<string> { "alpha", "beta" },
            });
            (await client.PostAsJsonAsync("/managed/request", update, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var attrs = await GetAttributes(client, space, subpath, shortname);
            // tags should be present and contain both values.
            attrs.ShouldContainKey("tags");
            var tagsEl = (JsonElement)attrs["tags"];
            tagsEl.ValueKind.ShouldBe(JsonValueKind.Array);
            var values = tagsEl.EnumerateArray().Select(e => e.GetString()).ToList();
            values.ShouldContain("alpha");
            values.ShouldContain("beta");
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Patch_IsActive_Bool_RoundTrips()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"activetst_{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname,
            attributes: new() { ["displayname"] = "is_active probe" });

        try
        {
            // Sanity: created entries default to is_active=true.
            var preAttrs = await GetAttributes(client, space, subpath, shortname);
            ExtractBool(preAttrs, "is_active").ShouldBe(true);

            var update = BuildUpdate(space, subpath, shortname, new()
            {
                ["is_active"] = false,
            });
            (await client.PostAsJsonAsync("/managed/request", update, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var postAttrs = await GetAttributes(client, space, subpath, shortname);
            ExtractBool(postAttrs, "is_active").ShouldBe(false);
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Patch_OwnerShortname_Is_Silently_Ignored()
    {
        var (client, _, ownerShortname, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"ownrtest_{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname,
            attributes: new() { ["displayname"] = "owner probe" });

        try
        {
            // Sneak in a different owner_shortname via the patch body. Python's
            // Meta.restricted_fields blocks this; the C# port must too.
            var update = BuildUpdate(space, subpath, shortname, new()
            {
                ["owner_shortname"] = "intruder",
                ["displayname"] = "still updating",
            });
            (await client.PostAsJsonAsync("/managed/request", update, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var attrs = await GetAttributes(client, space, subpath, shortname);
            attrs.ShouldContainKey("owner_shortname");
            // Owner must remain the original user, not the value sent in the patch.
            // attrs[...] is a JsonElement; GetString() unwraps the JSON quotes.
            ((JsonElement)attrs["owner_shortname"]).GetString().ShouldBe(ownerShortname);
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    // Patch with `"slug": null` must clear the column. Before the IsPatchNull
    // helper landed, `Str` saw JsonElement(Null) (the source-gen wire shape),
    // failed the `v is not null` guard incorrectly (struct value is never CLR
    // null), and wrote `el.ToString()` — yielding the empty string instead of
    // clearing the field. Pins the contract that null clears.
    [FactIfPg]
    public async Task Patch_Slug_With_Null_Clears_It()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"slugtst_{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";
        var initialSlug = $"slug-{Guid.NewGuid():N}".Substring(0, 12);

        await CreateContent(client, space, subpath, shortname,
            attributes: new() { ["displayname"] = "slug probe", ["slug"] = initialSlug });

        try
        {
            // Sanity: slug landed on create.
            var preAttrs = await GetAttributes(client, space, subpath, shortname);
            preAttrs.ShouldContainKey("slug");
            ((JsonElement)preAttrs["slug"]).GetString().ShouldBe(initialSlug);

            // Raw JSON so the wire carries `"slug": null` — source-gen will
            // land that as a JsonElement(Null), which is the shape under test.
            var patchJson = $$"""
                {
                    "request_type": "update",
                    "space_name": "{{space}}",
                    "records": [
                        {
                            "resource_type": "content",
                            "subpath": "{{subpath}}",
                            "shortname": "{{shortname}}",
                            "attributes": { "slug": null }
                        }
                    ]
                }
                """;
            using var httpContent = new StringContent(patchJson, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/managed/request", httpContent);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Slug is `string?`; cleared → JsonStripEmptiesMiddleware drops
            // the key. Specifically asserting "no key" is sharper than
            // "value is empty string" — empty string was the bug shape.
            var postAttrs = await GetAttributes(client, space, subpath, shortname);
            postAttrs.ShouldNotContainKey("slug");
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    // Patch with `"tags": null` must clear the column. Before the fix,
    // PatchTags's branch chain fell through to fallback on JsonElement(Null)
    // and silently kept the existing tags. Tags is non-nullable (`List<string>`
    // defaulting to []), so "clear" means empty list — which the strip-empties
    // middleware then removes from the response.
    [FactIfPg]
    public async Task Patch_Tags_With_Null_Clears_Them()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"tagsnul_{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname,
            attributes: new()
            {
                ["displayname"] = "tags-null probe",
                ["tags"] = new List<string> { "alpha", "beta" },
            });

        try
        {
            var preAttrs = await GetAttributes(client, space, subpath, shortname);
            ((JsonElement)preAttrs["tags"]).GetArrayLength().ShouldBe(2);

            var patchJson = $$"""
                {
                    "request_type": "update",
                    "space_name": "{{space}}",
                    "records": [
                        {
                            "resource_type": "content",
                            "subpath": "{{subpath}}",
                            "shortname": "{{shortname}}",
                            "attributes": { "tags": null }
                        }
                    ]
                }
                """;
            using var httpContent = new StringContent(patchJson, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/managed/request", httpContent);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var postAttrs = await GetAttributes(client, space, subpath, shortname);
            postAttrs.ShouldNotContainKey("tags");
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    // -- helpers --

    private static Request BuildUpdate(string space, string subpath, string shortname,
        Dictionary<string, object> attributes) =>
        new()
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
                    Attributes = attributes,
                },
            },
        };

    private static async Task CreateContent(HttpClient client, string space, string subpath,
        string shortname, Dictionary<string, object> attributes)
    {
        var req = new Request
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
                    Attributes = attributes,
                },
            },
        };
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Create failed: {resp.StatusCode}\n{body}");
        }
    }

    private static async Task DeleteContent(HttpClient client, string space, string subpath, string shortname)
    {
        try
        {
            var req = new Request
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
            await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        }
        catch { /* best-effort cleanup */ }
    }

    private static async Task<Dictionary<string, object>> GetAttributes(
        HttpClient client, string space, string subpath, string shortname)
    {
        var resp = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{shortname}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        // /managed/entry returns a single record's attributes flattened at
        // the top level (not wrapped in Response). Parse as a JsonElement
        // and project into Dictionary<string, object>.
        var root = JsonDocument.Parse(json).RootElement;
        var attrs = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
            attrs[prop.Name] = prop.Value;
        return attrs;
    }

    private static bool ExtractBool(Dictionary<string, object> attrs, string key)
    {
        attrs.ShouldContainKey(key);
        var el = (JsonElement)attrs[key];
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new Xunit.Sdk.XunitException($"{key} is not bool: ValueKind={el.ValueKind}"),
        };
    }
}
