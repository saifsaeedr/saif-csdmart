using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

public sealed class SavedQueryParityTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SavedQueryParityTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Managed_Excute_Accepts_Record_Body_And_Substitutes_Params()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var taskShortname = $"itest_report_{Guid.NewGuid():N}"[..24];

        var queryBody = JsonDocument.Parse("""
        {
          "type": "search",
          "space_name": "management",
          "subpath": "/users",
          "filter_types": ["user"],
          "filter_schema_names": [],
          "search": "@shortname:$who",
          "limit": 10
        }
        """).RootElement.Clone();

        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = taskShortname,
            SpaceName = "management",
            Subpath = "/reports",
            ResourceType = ResourceType.Content,
            OwnerShortname = "dmart",
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                SchemaShortname = "query",
                Body = queryBody,
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            var body = new StringContent($$"""
            {
              "resource_type": "content",
              "subpath": "/reports",
              "shortname": "{{taskShortname}}",
              "attributes": { "who": "dmart" }
            }
            """, Encoding.UTF8, "application/json");

            var resp = await user.Client.PostAsync("/managed/excute/query/management", body);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var response = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            response!.Status.ShouldBe(Status.Success);
            response.Records!.Select(r => r.Shortname).ShouldContain("dmart");
        }
        finally
        {
            try { await entries.DeleteAsync("management", "/reports", taskShortname, ResourceType.Content); } catch { }
            await user.Cleanup();
        }
    }

    // Seed-data saved queries use schema_shortname="api" and wrap the query in
    // `request_body`. The handler must unwrap that, otherwise deserialization
    // fails on missing required fields (`type`, `space_name`, `subpath`).
    [FactIfPg]
    public async Task Managed_Execute_Unwraps_Api_Schema_RequestBody()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var taskShortname = $"itest_api_{Guid.NewGuid():N}"[..24];

        var apiBody = JsonDocument.Parse("""
        {
          "end_point": "/managed/query",
          "verb": "post",
          "request_body": {
            "type": "search",
            "space_name": "management",
            "subpath": "/users",
            "filter_types": ["user"],
            "filter_schema_names": [],
            "search": "@shortname:dmart",
            "limit": 10
          }
        }
        """).RootElement.Clone();

        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = taskShortname,
            SpaceName = "management",
            Subpath = "/reports",
            ResourceType = ResourceType.Content,
            OwnerShortname = "dmart",
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                SchemaShortname = "api",
                Body = apiBody,
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            var body = new StringContent($$"""
            { "shortname": "{{taskShortname}}", "subpath": "/reports" }
            """, Encoding.UTF8, "application/json");

            var resp = await user.Client.PostAsync("/managed/execute/query/management", body);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var response = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            response!.Status.ShouldBe(Status.Success);
            response.Records!.Select(r => r.Shortname).ShouldContain("dmart");
        }
        finally
        {
            try { await entries.DeleteAsync("management", "/reports", taskShortname, ResourceType.Content); } catch { }
            await user.Cleanup();
        }
    }
}
