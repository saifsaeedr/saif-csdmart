using Microsoft.OpenApi;

namespace Dmart.Api;

// Multipart request-body schemas injected into the OpenAPI document at
// startup, one entry per multipart endpoint. Avoids the AOT problems of
// per-endpoint `.WithOpenApi()` (IL2026/IL3050 — uses reflection) and the
// source-gen problems of `.Accepts<TForm>("multipart/form-data")` (IFormFile
// is an interface, source-gen can't emit type metadata for it).
//
// Adding a new multipart endpoint? Add an entry to `_endpoints` and the
// schema lands in the OpenAPI doc next time the server starts.
internal static class OpenApiMultipartSchemas
{
    private sealed record FormField(string Name, bool Binary, bool Required);

    private sealed record EndpointForm(
        string Method,
        string Path,
        string Description,
        FormField[] Fields);

    private static readonly EndpointForm[] _endpoints = new[]
    {
        new EndpointForm("post", "/managed/import",
            "Restore a space from a dmart export ZIP.",
            new[] { new FormField("zip_file", Binary: true, Required: true) }),

        new EndpointForm("post", "/managed/resource_with_payload",
            "Create or update an entry / attachment from a multipart upload. " +
            "`payload_file` is the binary or JSON content; `request_record` is a " +
            "JSON-encoded core.Record describing the entry.",
            new[]
            {
                new FormField("payload_file",   Binary: true,  Required: true),
                new FormField("request_record", Binary: true,  Required: true),
                new FormField("space_name",     Binary: false, Required: true),
                new FormField("sha",            Binary: false, Required: false),
            }),

        // ASP.NET routing template uses `{**rest}` (catch-all greedy
        // segment) but OpenAPI has no equivalent shape — the generator
        // normalizes the path to `{rest}` in the emitted doc. Use the
        // normalized form here or the path lookup in Apply() silently
        // no-ops and the endpoint's Swagger entry shows no body schema.
        // Pinned by OpenApiDocumentTests.OpenApi_MultipartEndpoints_*.
        new EndpointForm("post", "/managed/resources_from_csv/{resource_type}/{space}/{rest}",
            "Bulk-create or bulk-update entries from a CSV file (one row per entry).",
            new[] { new FormField("resources_file", Binary: true, Required: true) }),

        new EndpointForm("post", "/public/resource_with_payload",
            "Anonymous variant of /managed/resource_with_payload. Subject to the " +
            "ALLOWED_SUBMIT_MODELS gate.",
            new[]
            {
                new FormField("payload_file",   Binary: true,  Required: true),
                new FormField("request_record", Binary: true,  Required: true),
                new FormField("space_name",     Binary: false, Required: true),
                new FormField("sha",            Binary: false, Required: false),
            }),

        new EndpointForm("post", "/public/attach/{space_name}",
            "Anonymous attachment upload. `record` is the JSON-encoded " +
            "core.Record; `payload_file` is optional and supplies the file bytes.",
            new[]
            {
                new FormField("record",       Binary: false, Required: true),
                new FormField("payload_file", Binary: true,  Required: false),
            }),
    };

    public static void Apply(OpenApiDocument document)
    {
        if (document.Paths is null) return;
        foreach (var ep in _endpoints)
        {
            if (!document.Paths.TryGetValue(ep.Path, out var pathItem)) continue;
            if (pathItem.Operations is null) continue;
            foreach (var (method, op) in pathItem.Operations)
            {
                if (!method.ToString().Equals(ep.Method, StringComparison.OrdinalIgnoreCase))
                    continue;
                op.RequestBody = Build(ep);
            }
        }
    }

    private static OpenApiRequestBody Build(EndpointForm ep)
    {
        var props = new Dictionary<string, IOpenApiSchema>();
        var required = new HashSet<string>();
        foreach (var f in ep.Fields)
        {
            props[f.Name] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = f.Binary ? "binary" : null,
            };
            if (f.Required) required.Add(f.Name);
        }
        return new OpenApiRequestBody
        {
            Required = true,
            Description = ep.Description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = required,
                        Properties = props,
                    },
                },
            },
        };
    }
}
