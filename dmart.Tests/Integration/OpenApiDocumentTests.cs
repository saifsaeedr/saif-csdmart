using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Smoke tests for the generated OpenAPI document at /docs/openapi.json.
//
// The doc is built by .NET 10's AddOpenApi pipeline + the three transformers
// wired up in Program.cs (enum schemas from EnumMemberConverters.All, sample
// payloads from OpenApiExamples, multipart bodies from OpenApiMultipartSchemas).
// Visual /docs verification is necessary but not sufficient — these tests pin
// the regression modes that visual inspection misses:
//
//   * a `$ref` to a component that doesn't exist (Swagger UI prints "Could
//     not resolve reference: X" — easy to miss until a user reports it)
//   * a multipart endpoint that didn't get its schema injected (the path was
//     renamed in a handler but the OpenApiMultipartSchemas entry wasn't
//     updated — Swagger shows the path with no body schema)
//   * an enum reachable from an endpoint but missing from EnumMemberConverters.All
//     (Swagger emits the $ref without a matching components.schemas entry)
//
// Doesn't fixture-depend on the DB (the OpenAPI doc generates from registered
// endpoints + DI types, not from runtime state), but DmartFactory is the
// existing TestServer factory — using it keeps the suite consistent.
public sealed class OpenApiDocumentTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OpenApiDocumentTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task OpenApi_Doc_Loads_With_Paths_And_Components()
    {
        var doc = await FetchAsync();

        doc.TryGetProperty("paths", out var paths).ShouldBeTrue("OpenAPI doc must have a `paths` object");
        paths.ValueKind.ShouldBe(JsonValueKind.Object);
        paths.EnumerateObject().Any().ShouldBeTrue("at least one path must be registered");

        doc.TryGetProperty("components", out var components).ShouldBeTrue("OpenAPI doc must have a `components` object");
        components.TryGetProperty("schemas", out var schemas).ShouldBeTrue("components.schemas must exist");
        schemas.EnumerateObject().Any().ShouldBeTrue("at least one component schema must be registered");
    }

    // Every $ref in the doc must resolve to a defined components/schemas entry.
    // This is the load-bearing assertion that catches:
    //   - an enum reachable from an endpoint but missing from EnumMemberConverters.All
    //   - a DocsDto type that doesn't have its source-gen JsonSerializable entry
    //   - a removed schema whose $ref site wasn't updated
    [Fact]
    public async Task OpenApi_AllSchemaRefs_Resolve_To_Defined_Components()
    {
        var doc = await FetchAsync();
        var defined = doc.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).ToHashSet();

        var unresolved = new List<string>();
        WalkRefs(doc, refPath =>
        {
            // We only care about internal schema refs — external refs
            // (RFC 3986 URIs, JSON Schema vocabularies) are out of scope.
            const string prefix = "#/components/schemas/";
            if (!refPath.StartsWith(prefix, StringComparison.Ordinal)) return;
            var name = refPath[prefix.Length..];
            if (!defined.Contains(name)) unresolved.Add(refPath);
        });

        unresolved.ShouldBeEmpty(
            $"unresolved $ref(s) — Swagger UI would show 'Could not resolve reference' for each:\n  {string.Join("\n  ", unresolved.Distinct())}");
    }

    // Every enum declared in EnumMemberConverters.All must be defined under
    // components.schemas with type=string and a non-empty enum list. The
    // transformer in Program.cs is supposed to inject these (the .NET 10
    // generator can't introspect [JsonConverter]-decorated enums).
    [Fact]
    public async Task OpenApi_EveryDeclaredEnum_Is_Defined_AsStringWithValues()
    {
        var doc = await FetchAsync();
        var schemas = doc.GetProperty("components").GetProperty("schemas");

        foreach (var (schemaName, wireValues) in Dmart.Models.Json.EnumMemberConverters.All)
        {
            schemas.TryGetProperty(schemaName, out var schema).ShouldBeTrue(
                $"components.schemas.{schemaName} is missing — was a new EnumMemberConverter added without an entry in EnumMemberConverters.All?");
            schema.GetProperty("type").GetString().ShouldBe("string", $"schema {schemaName} must be type=string");
            schema.TryGetProperty("enum", out var enumArr).ShouldBeTrue($"schema {schemaName} must carry an `enum` array");
            var values = enumArr.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            values.ShouldBe(wireValues.ToList(), $"schema {schemaName}.enum must match WireValues exactly");
        }
    }

    // Every multipart endpoint declared in OpenApiMultipartSchemas._endpoints
    // must (a) resolve to a path in the document and (b) have a
    // multipart/form-data request body with the expected fields. Catches the
    // "path was renamed in a handler but the inject entry wasn't updated"
    // case — Swagger would then show the endpoint with no body schema.
    [Fact]
    public async Task OpenApi_MultipartEndpoints_Have_Multipart_RequestBody()
    {
        var doc = await FetchAsync();
        var paths = doc.GetProperty("paths");

        // Same path list as OpenApiMultipartSchemas._endpoints. Keep in sync —
        // if that array grows, this test catches missing schemas; if a path
        // here is removed from the source, this test must follow.
        var expected = new[]
        {
            "/managed/import",
            "/managed/resource_with_payload",
            // ASP.NET `{**rest}` normalized to `{rest}` in the OpenAPI doc;
            // OpenApiMultipartSchemas._endpoints uses the normalized form too.
            "/managed/resources_from_csv/{resource_type}/{space}/{rest}",
            "/public/resource_with_payload",
            "/public/attach/{space_name}",
        };

        foreach (var path in expected)
        {
            paths.TryGetProperty(path, out var pathItem).ShouldBeTrue($"path {path} not in OpenAPI doc — was the handler renamed without updating OpenApiMultipartSchemas?");
            pathItem.TryGetProperty("post", out var post).ShouldBeTrue($"path {path} has no POST operation");
            post.TryGetProperty("requestBody", out var body).ShouldBeTrue($"path {path} POST has no requestBody — multipart inject didn't fire");
            body.GetProperty("content").TryGetProperty("multipart/form-data", out var media).ShouldBeTrue(
                $"path {path} POST requestBody is missing the multipart/form-data content type");
            media.GetProperty("schema").GetProperty("type").GetString().ShouldBe("object",
                $"path {path} multipart schema must be type=object");
        }
    }

    // Spot-check on a handful of endpoints that should carry a JSON request
    // body. If the .Accepts<T>("application/json") annotation gets dropped
    // from a handler, the body schema vanishes from Swagger and the "Try
    // it out" form shows no payload — this test catches the regression.
    [Theory]
    [InlineData("/managed/query")]
    [InlineData("/managed/request")]
    [InlineData("/user/login")]
    [InlineData("/user/profile")]
    [InlineData("/managed/semantic-search")]
    [InlineData("/managed/reindex-embeddings")]
    public async Task OpenApi_KeyEndpoint_HasJsonRequestBody(string path)
    {
        var doc = await FetchAsync();
        var paths = doc.GetProperty("paths");
        paths.TryGetProperty(path, out var pathItem).ShouldBeTrue($"path {path} not registered");
        pathItem.TryGetProperty("post", out var post).ShouldBeTrue($"path {path} has no POST operation");
        post.TryGetProperty("requestBody", out var body).ShouldBeTrue(
            $"path {path} has no requestBody — was .Accepts<T>(\"application/json\") dropped?");
        body.GetProperty("content").TryGetProperty("application/json", out _).ShouldBeTrue(
            $"path {path} requestBody is missing application/json content type");
    }

    // ----- helpers -----

    private async Task<JsonElement> FetchAsync()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/docs/openapi.json");
        resp.IsSuccessStatusCode.ShouldBeTrue($"GET /docs/openapi.json returned {(int)resp.StatusCode}");
        var json = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // Recursive walk of every "$ref" string in the document tree. Callback is
    // invoked once per ref site; same path may be visited multiple times if
    // the document references it more than once.
    private static void WalkRefs(JsonElement el, Action<string> onRef)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Name == "$ref" && prop.Value.ValueKind == JsonValueKind.String)
                        onRef(prop.Value.GetString()!);
                    else
                        WalkRefs(prop.Value, onRef);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    WalkRefs(item, onRef);
                break;
        }
    }
}
