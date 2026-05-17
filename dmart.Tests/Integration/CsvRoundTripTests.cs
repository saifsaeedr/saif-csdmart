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
        // Per-test user with super_admin role — see DmartFactory.CreateLoggedInUserAsync.
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

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
            // Three data rows + one deliberately malformed row:
            //   apple   — plain values + a JSON-array `features` cell
            //   banana  — quoted field with embedded comma + JSON-array `features`
            //   cherry  — quoted field with escaped double-quote + a `features`
            //             cell that *looks* like an array but is malformed JSON
            //             (must fall back to the raw string, mirroring Python's
            //             always-on heuristic in api/managed/utils.py:1553-1557)
            //
            // Plus a malformed row with the wrong column count so ImportAsync's
            // "expected N fields" failure branch is exercised.
            var csv =
                "shortname,name,price,in_stock,features\r\n" +
                "apple,Red Apple,1.25,true,\"[\"\"crisp\"\",\"\"sweet\"\"]\"\r\n" +
                "banana,\"Cavendish, Ripe\",0.75,true,\"[\"\"yellow\"\"]\"\r\n" +
                "cherry,\"Cherry \"\"Bing\"\"\",2.50,false,[not json\r\n" +
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
                """{"space_name":"itest_csv","type":"subpath","subpath":"items","filter_schema_names":[],"retrieve_json_payload":true,"limit":50}""");
            queryResp.Status.ShouldBe(Status.Success);
            // apple + banana + cherry (from CSV import) + rich_row = at least 4
            (queryResp.Records?.Count ?? 0).ShouldBeGreaterThanOrEqualTo(4);
            queryResp.Records!.Any(r => r.Shortname == "apple").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "banana").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "cherry").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "rich_row").ShouldBeTrue();

            // ---- 6. JSON-array heuristic ---------------------------------
            // apple's `features` cell was `["crisp","sweet"]`. The always-on
            // heuristic in CsvService.ParseCellValue must have lifted it into a
            // real JSON array — not a quoted string — so payload.body.features
            // round-trips as JsonValueKind.Array.
            var appleBody = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "apple"));
            var appleFeatures = appleBody.GetProperty("features");
            appleFeatures.ValueKind.ShouldBe(JsonValueKind.Array);
            appleFeatures.EnumerateArray().Select(e => e.GetString()).ToArray()
                .ShouldBe(new[] { "crisp", "sweet" });

            // cherry's `features` cell was `[not json` — invalid JSON that the
            // heuristic must fall back from, storing the raw string (Python parity).
            var cherryFeatures = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "cherry"))
                .GetProperty("features");
            cherryFeatures.ValueKind.ShouldBe(JsonValueKind.String);
            cherryFeatures.GetString().ShouldBe("[not json");
        }
        finally
        {
            await CleanupAsync(client);
        }
    }

    // CSV header lookup for the `shortname` column is OrdinalIgnoreCase by design —
    // `Shortname` and `SHORTNAME` are accepted as aliases of `shortname`. Pins that
    // contract so a future refactor doesn't silently revert to case-sensitive matching
    // (which is what the dictionary-based lookup used pre-#24).
    [FactIfPg]
    public async Task Csv_Import_UppercaseShortnameHeader_Works()
    {
        const string space = "itest_csv_upper";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Capital-S header — the row's shortname cell `peach` must end up as the
            // resulting record's shortname, not an auto-generated `row-…` value.
            var csv = "Shortname,name,price\r\npeach,Yellow Peach,3.50\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv));
            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(1);

            var queryBody = "{\"space_name\":\"" + space +
                "\",\"type\":\"subpath\",\"subpath\":\"items\",\"filter_schema_names\":[],\"limit\":50}";
            var queryResp = await PostJson(client, "/managed/query", queryBody);
            queryResp.Status.ShouldBe(Status.Success);
            queryResp.Records!.Any(r => r.Shortname == "peach").ShouldBeTrue(
                "row's shortname cell should win — case-insensitive header match must read the raw cell");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // ImportAsync auto-generates a `row-<12-hex>` shortname when the shortname cell is
    // empty (Guid.NewGuid().ToString("N").Substring(0, 12)). #24 dropped the prior
    // fixture row covering this branch; re-add explicit coverage.
    [FactIfPg]
    public async Task Csv_Import_EmptyShortnameCell_AutoGenerates()
    {
        const string space = "itest_csv_auto";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Two rows, both with empty shortname cells. Each must get its own
            // unique auto-generated shortname so the inserts don't collide.
            var csv =
                "shortname,name,price\r\n" +
                ",Plum,1.00\r\n" +
                ",Apricot,2.00\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv));
            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(2);

            var queryBody = "{\"space_name\":\"" + space +
                "\",\"type\":\"subpath\",\"subpath\":\"items\",\"filter_schema_names\":[],\"limit\":50}";
            var queryResp = await PostJson(client, "/managed/query", queryBody);
            queryResp.Status.ShouldBe(Status.Success);
            var autoNames = queryResp.Records!
                .Select(r => r.Shortname)
                .Where(s => s.StartsWith("row-", StringComparison.Ordinal))
                .ToList();
            autoNames.Count.ShouldBe(2);
            // ImportAsync builds `row-{Guid.NewGuid():N}`.Substring(0, 12) →
            // 4-char "row-" prefix + 8 hex chars from the start of the guid = 12 total.
            foreach (var n in autoNames)
                System.Text.RegularExpressions.Regex.IsMatch(n, "^row-[0-9a-f]{8}$")
                    .ShouldBeTrue($"auto-generated shortname '{n}' must match row-<8hex>");
            autoNames.Distinct().Count().ShouldBe(2, "auto-generated shortnames must be unique");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // `?is_update=true` switches the route from per-row create to per-row
    // UpdateAsync, deep-merging the CSV's columns into the existing entry's
    // payload.body. Pre-existing fields not mentioned in the CSV row must
    // survive; conflicting fields win in favor of the CSV value.
    [FactIfPg]
    public async Task Csv_Import_UpdateMode_DeepMerges_PayloadBody()
    {
        const string space = "itest_csv_upd";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Seed two entries with full payload.body. The CSV update below
            // only sends a subset of keys per row — the keys it omits must
            // remain unchanged after the deep merge.
            (await PostOk(client, "/managed/request",
                "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"subpath\":\"items\",\"shortname\":\"alpha\"," +
                "\"attributes\":{\"payload\":{\"content_type\":\"json\",\"schema_shortname\":\"goods\"," +
                "\"body\":{\"name\":\"Alpha original\",\"price\":1.00,\"in_stock\":true}}}}]}"))
                .ShouldBeTrue("alpha seed");
            (await PostOk(client, "/managed/request",
                "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"subpath\":\"items\",\"shortname\":\"beta\"," +
                "\"attributes\":{\"payload\":{\"content_type\":\"json\",\"schema_shortname\":\"goods\"," +
                "\"body\":{\"name\":\"Beta original\",\"price\":2.00,\"in_stock\":true}}}}]}"))
                .ShouldBeTrue("beta seed");

            // Update CSV touches only `price` (overwrite) and adds `color` (new key).
            // `name` and `in_stock` columns are omitted — they must survive untouched.
            var csv =
                "shortname,price,color\r\n" +
                "alpha,9.99,red\r\n" +
                "beta,8.88,blue\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv), isUpdate: true);

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(2);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(0);

            // Verify the merge: original `name`/`in_stock` survive, `price` is
            // overwritten, `color` is added.
            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");
            queryResp.Status.ShouldBe(Status.Success);

            var alpha = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "alpha"));
            alpha.GetProperty("name").GetString().ShouldBe("Alpha original");
            alpha.GetProperty("in_stock").GetBoolean().ShouldBeTrue();
            // CSV cells come through as strings (no schema-driven type coercion —
            // pinning Python parity behavior, see ParseCellValue comment).
            alpha.GetProperty("price").GetString().ShouldBe("9.99");
            alpha.GetProperty("color").GetString().ShouldBe("red");

            var beta = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "beta"));
            beta.GetProperty("name").GetString().ShouldBe("Beta original");
            beta.GetProperty("in_stock").GetBoolean().ShouldBeTrue();
            beta.GetProperty("price").GetString().ShouldBe("8.88");
            beta.GetProperty("color").GetString().ShouldBe("blue");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // Update mode against a CSV row whose shortname doesn't exist: the
    // surrounding request must still return Status.Success (it's a partial-batch
    // contract, not a fail-the-whole-import contract) but the missing row lands
    // in `failed` with EntryService's OBJECT_NOT_FOUND. Symmetric with the
    // create branch's SHORTNAME_ALREADY_EXIST handling.
    [FactIfPg]
    public async Task Csv_Import_UpdateMode_MissingShortname_Returns_ObjectNotFound_In_Failed_List()
    {
        const string space = "itest_csv_upd_miss";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Single row pointing at a shortname that was never created.
            var csv = "shortname,name\r\nghost,Nothing here\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv), isUpdate: true);

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(0);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(1);

            // `failed` round-trips as a JsonElement array via the source-gen
            // dict<string,object> path. Inspect the single entry's code +
            // shortname so a regression that returns a different InternalErrorCode
            // (e.g. NOT_ALLOWED for a permission miss) is caught explicitly.
            var failedEl = (JsonElement)importResp.Attributes!["failed"];
            failedEl.GetArrayLength().ShouldBe(1);
            var row0 = failedEl[0];
            row0.GetProperty("shortname").GetString().ShouldBe("ghost");
            // Either int (raw) or string (round-tripped via JsonElement.GetRawText)
            // depending on path — accept both shapes defensively.
            var code = row0.GetProperty("code");
            var codeAsInt = code.ValueKind == JsonValueKind.Number
                ? code.GetInt32()
                : int.Parse(code.GetString()!);
            codeAsInt.ShouldBe(Dmart.Models.Api.InternalErrorCode.OBJECT_NOT_FOUND,
                "missing-shortname update row should map to OBJECT_NOT_FOUND");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // In create mode an empty shortname cell auto-generates `row-<8hex>`. In
    // update mode the same auto-generation happens but no entry with that
    // freshly-minted shortname exists yet → every empty-shortname row 404s
    // into the failed list. Pin that contract so a future change that decides
    // to (e.g.) silently skip empty-shortname rows under update mode is loud.
    [FactIfPg]
    public async Task Csv_Import_UpdateMode_EmptyShortnameCell_Falls_Into_Failed_List()
    {
        const string space = "itest_csv_upd_empty";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Both rows have empty shortname cells; update mode auto-generates
            // names that don't exist anywhere → both fail.
            var csv =
                "shortname,name\r\n" +
                ",Orphan A\r\n" +
                ",Orphan B\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv), isUpdate: true);

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(0);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(2);
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // ---------------- helpers ----------------

    // Sets up space + items folder + schema folder + a permissive `goods` schema
    // for the per-test isolated spaces used by the case-sensitivity / auto-shortname
    // tests. The original Csv_Import_Then_Export test inlines this against `itest_csv`
    // and isn't refactored to use this helper.
    private static async Task SeedSpaceAsync(HttpClient client, string space)
    {
        (await PostOk(client, "/managed/request",
            "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"" + space +
            "\",\"attributes\":{\"hide_space\":true,\"is_active\":true}}]}"))
            .ShouldBeTrue("space create");
        (await PostOk(client, "/managed/request",
            "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"items\"," +
            "\"attributes\":{\"is_active\":true}}]}"))
            .ShouldBeTrue("folder create");
        // schema/ may already exist (auto-created by resource_folders_creation plugin)
        await PostOk(client, "/managed/request",
            "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"schema\"," +
            "\"attributes\":{\"is_active\":true}}]}");
        await UploadSchemaAsync(client,
            shortname: "goods",
            schemaJson: """{"title":"goods","type":"object","additionalProperties":true}""",
            space: space);
    }

    private static async Task CleanupAsync(HttpClient client, string space = "itest_csv")
    {
        // Fire-and-forget — ok if the space doesn't exist yet.
        var body = "{\"space_name\":\"" + space + "\",\"request_type\":\"delete\",\"records\":[" +
                   "{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"" + space + "\",\"attributes\":{}}]}";
        using var _ = await client.PostAsync("/managed/request", new StringContent(
            body, Encoding.UTF8, "application/json"));
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

    private static async Task UploadSchemaAsync(HttpClient client, string shortname, string schemaJson,
        string space = "itest_csv")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(space), "space_name");

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
        HttpClient client, string resourceType, string space, string subpath, string schema, byte[] csvBytes,
        bool isUpdate = false)
    {
        using var form = new MultipartFormDataContent();
        var csvPart = new ByteArrayContent(csvBytes);
        csvPart.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(csvPart, "resources_file", "rows.csv");

        var url = $"/managed/resources_from_csv/{resourceType}/{space}/{subpath}/{schema}";
        if (isUpdate) url += "?is_update=true";
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

    // After /managed/query with retrieve_json_payload=true, record.Attributes["payload"]
    // is a JsonElement (System.Text.Json source-gen materializes `object` values as
    // JsonElement on the deserialize side). Drill in one level to payload.body.
    private static JsonElement GetPayloadBody(Record record)
    {
        record.Attributes.ShouldNotBeNull();
        var payload = (JsonElement)record.Attributes!["payload"];
        return payload.GetProperty("body");
    }
}
