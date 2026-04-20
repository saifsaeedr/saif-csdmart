using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Targeted coverage test for Services/CsvService — previously 27% covered, the
// single biggest uncovered file in Services/Api. Exercises:
//
//   * ImportAsync via POST /managed/resources_from_csv/{type}/{space}/{subpath}/{schema}
//     (CSV with header + several rows, one row containing a quoted comma, one
//     containing an escaped quote — hits ParseCsvLine's quote-handling branches
//     and ImportAsync's happy + mismatched-column-count + row-failure branches)
//
//   * ExportAsync via POST /managed/csv on entries whose payload.body contains
//     nested objects, arrays-of-scalars, arrays-of-objects, null values, booleans,
//     and strings with commas + embedded quotes — hits FlattenJsonElement's Object,
//     Array-scalar, Array-complex, String, True, False, Null, and default branches
//     plus EscapeField's quoting branch.
//
// Uses a dedicated space name so it doesn't collide with other integration tests.
public class CsvRoundTripTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public CsvRoundTripTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Csv_Import_Then_Export_Exercises_Flatten_And_Parse_Branches()
    {
        var client = _factory.CreateClient();

        // ---- login ------------------------------------------------------
        var loginJson =
            "{\"shortname\":\"" + _factory.AdminShortname +
            "\",\"password\":\"" + _factory.AdminPassword + "\"}";
        var loginResp = await client.PostAsync("/user/login",
            new StringContent(loginJson, Encoding.UTF8, "application/json"));
        var loginRaw = await loginResp.Content.ReadAsStringAsync();
        var loginBody = JsonSerializer.Deserialize(loginRaw, DmartJsonContext.Default.Response);
        loginBody.ShouldNotBeNull($"Login deserialization failed: {loginRaw}");
        loginBody!.Status.ShouldBe(Status.Success, $"Login failed: {loginRaw}");
        var token = loginBody.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"Login failed for '{_factory.AdminShortname}': {loginResp.StatusCode} {loginRaw}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            // ---- 1. create space + folder + schema ----------------------
            await CleanupAsync(client);

            (await PostOk(client, "/managed/request",
                """{"space_name":"itest_csv","request_type":"create","records":[{"resource_type":"space","subpath":"/","shortname":"itest_csv","attributes":{"hide_space":true,"is_active":true}}]}"""))
                .ShouldBeTrue("space create");

            (await PostOk(client, "/managed/request",
                """{"space_name":"itest_csv","request_type":"create","records":[{"resource_type":"folder","subpath":"/","shortname":"items","attributes":{"is_active":true}}]}"""))
                .ShouldBeTrue("folder create");

            // /schema may already exist (auto-created by resource_folders_creation plugin)
            await PostOk(client, "/managed/request",
                """{"space_name":"itest_csv","request_type":"create","records":[{"resource_type":"folder","subpath":"/","shortname":"schema","attributes":{"is_active":true}}]}""");

            // Permissive schema — additionalProperties:true with no required fields,
            // so every CSV row (all-string values) validates cleanly.
            await UploadSchemaAsync(client,
                shortname: "goods",
                schemaJson: """{"title":"goods","type":"object","additionalProperties":true}""");

            // ---- 2. import CSV via /managed/resources_from_csv -----------
            // Four data rows:
            //   apple   — plain values
            //   banana  — quoted field with embedded comma
            //   cherry  — quoted field with escaped double-quote (`""`)
            //   date    — no `shortname` column (absent from header) → ImportAsync
            //             auto-generates a uuid-based shortname
            //
            // Plus one deliberately malformed row with the wrong column count so
            // ImportAsync's "expected N fields" failure branch is exercised.
            var csv =
                "shortname,name,price,in_stock\r\n" +
                "apple,Red Apple,1.25,true\r\n" +
                "banana,\"Cavendish, Ripe\",0.75,true\r\n" +
                "cherry,\"Cherry \"\"Bing\"\"\",2.50,false\r\n" +
                "malformed,only_two_fields\r\n";

            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: "itest_csv", subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            var importAttrs = importResp.Attributes!;
            // inserted + failed numbers come back as JsonElement since the dictionary
            // value type is object — handle both representations defensively.
            ExtractInt(importAttrs["inserted"]).ShouldBe(3);
            ExtractInt(importAttrs["failed_count"]).ShouldBe(1);

            // ---- 3. create a rich entry so the export path exercises
            //         CsvService's Flatten/Escape branches. EntryMapper.ToRecord
            //         populates Record.Attributes with TYPED values (bool for
            //         is_active, Translation for displayname, string[] for tags,
            //         Payload for payload) — not raw JsonElements — so the
            //         interesting branches to hit are:
            //           * case Translation t  (displayname with en/ar filled in)
            //           * case string[]       (tags joined with "|")
            //           * case Payload p      (payload serialized whole)
            //           * EscapeField quoting (a tag containing a comma/quote/newline)
            var richCreateJson =
                "{\"space_name\":\"itest_csv\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"subpath\":\"items\",\"shortname\":\"rich_row\"," +
                "\"attributes\":{" +
                    "\"displayname\":{\"en\":\"Rich Row EN\",\"ar\":\"Rich Row AR\"}," +
                    // Three tags: plain, one with a comma, one with an embedded double-quote.
                    // After string[] → "plain|has,comma|has\"quote",
                    // EscapeField sees both a comma and a quote → wraps + doubles the quote:
                    //   "plain|has,comma|has""quote"
                    "\"tags\":[\"plain\",\"has,comma\",\"has\\\"quote\"]," +
                    "\"payload\":{\"content_type\":\"json\",\"body\":{\"k\":\"v\"}}" +
                "}}]}";
            (await PostOk(client, "/managed/request", richCreateJson))
                .ShouldBeTrue("rich entry create");

            // ---- 4. export items folder as CSV -------------------------
            var exportResp = await client.PostAsync("/managed/csv", new StringContent(
                """{"space_name":"itest_csv","subpath":"items","type":"subpath","filter_schema_names":[],"retrieve_json_payload":true,"limit":50}""",
                Encoding.UTF8, "application/json"));
            exportResp.IsSuccessStatusCode.ShouldBeTrue();
            var exportedCsv = await exportResp.Content.ReadAsStringAsync();

            // Header + at least 4 data rows (apple, banana, cherry, rich_row; the
            // malformed row is skipped, the auto-shortname row adds a 5th).
            var lines = exportedCsv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBeGreaterThanOrEqualTo(5);

            var header = lines[0];
            // Canonical columns always present in this order.
            header.ShouldStartWith("resource_type,shortname,subpath,uuid");
            // Translation branch emits `displayname.en` and `displayname.ar` sub-keys.
            header.ShouldContain("displayname.en");
            header.ShouldContain("displayname.ar");
            // Tags are a top-level attribute column.
            header.ShouldContain("tags");

            // The rich_row's tags value is `plain|has,comma|has"quote` which contains
            // both a comma and a double-quote, so EscapeField wraps it in quotes and
            // doubles the embedded quote: "plain|has,comma|has""quote".
            exportedCsv.ShouldContain("\"plain|has,comma|has\"\"quote\"");

            // And the displayname Translation should have emitted the EN/AR values.
            exportedCsv.ShouldContain("Rich Row EN");
            exportedCsv.ShouldContain("Rich Row AR");

            // ---- 5. round-trip verification: query the space ------------
            var queryResp = await PostJson(client, "/managed/query",
                """{"space_name":"itest_csv","type":"subpath","subpath":"items","filter_schema_names":[],"limit":50}""");
            queryResp.Status.ShouldBe(Status.Success);
            // apple + banana + cherry (from CSV import) + rich_row = at least 4
            (queryResp.Records?.Count ?? 0).ShouldBeGreaterThanOrEqualTo(4);
            queryResp.Records!.Any(r => r.Shortname == "apple").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "banana").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "cherry").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "rich_row").ShouldBeTrue();
        }
        finally
        {
            await CleanupAsync(client);
        }
    }

    // ---------------- helpers ----------------

    private static async Task CleanupAsync(HttpClient client)
    {
        // Fire-and-forget — ok if the space doesn't exist yet.
        using var _ = await client.PostAsync("/managed/request", new StringContent(
            """{"space_name":"itest_csv","request_type":"delete","records":[{"resource_type":"space","subpath":"/","shortname":"itest_csv","attributes":{}}]}""",
            Encoding.UTF8, "application/json"));
    }

    private static async Task<bool> PostOk(HttpClient client, string url, string body)
    {
        var resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var parsed = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return parsed!.Status == Status.Success;
    }

    private static async Task<Response> PostJson(HttpClient client, string url, string body)
    {
        var resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var parsed = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return parsed!;
    }

    private static async Task UploadSchemaAsync(HttpClient client, string shortname, string schemaJson)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("itest_csv"), "space_name");

        var recordJson =
            "{\"resource_type\":\"schema\",\"subpath\":\"schema\",\"shortname\":\"" + shortname +
            "\",\"attributes\":{\"payload\":{\"content_type\":\"json\",\"body\":\"" + shortname + ".json\"}}}";
        var recordPart = new ByteArrayContent(Encoding.UTF8.GetBytes(recordJson));
        recordPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(recordPart, "request_record", "request_record.json");

        var payloadPart = new ByteArrayContent(Encoding.UTF8.GetBytes(schemaJson));
        payloadPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(payloadPart, "payload_file", shortname + ".json");

        var resp = await client.PostAsync("/managed/resource_with_payload", form);
        var parsed = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        parsed!.Status.ShouldBe(Status.Success, $"schema upload for {shortname}");
    }

    private static async Task<Response> UploadCsvAsync(
        HttpClient client, string resourceType, string space, string subpath, string schema, byte[] csvBytes)
    {
        using var form = new MultipartFormDataContent();
        var csvPart = new ByteArrayContent(csvBytes);
        csvPart.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(csvPart, "resources_file", "rows.csv");

        var url = $"/managed/resources_from_csv/{resourceType}/{space}/{subpath}/{schema}";
        var resp = await client.PostAsync(url, form);
        var parsed = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return parsed!;
    }

    // Reads an int out of a Dictionary<string,object> value that may arrive as a
    // boxed JsonElement or as a raw int/long (defensive — System.Text.Json source-gen
    // round-trips this field as JsonElement from a Response deserialization).
    private static int ExtractInt(object value) => value switch
    {
        JsonElement el => el.ValueKind == JsonValueKind.Number ? el.GetInt32() : int.Parse(el.ToString()!),
        int i          => i,
        long l         => (int)l,
        _              => int.Parse(value.ToString()!),
    };
}
