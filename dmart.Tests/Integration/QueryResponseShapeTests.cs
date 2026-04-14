using System.Linq;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Regression tests for the /managed/query response shape. Pins the Python-
// parity facts discovered while comparing the C# port against dmart Python
// running against the same PostgreSQL database:
//
//   - Response envelope: {status, records, attributes}
//     * error is omitted on success (no "error": null leak)
//   - attributes: {"total": N, "returned": M}
//     * total  = total matching rows IGNORING limit/offset
//     * returned = count on this page (equals len(records))
//   - Record envelope: {resource_type, uuid, shortname, subpath, attributes, retrieve_lock_status}
//     * attachments is omitted unless non-null
//   - Record.attributes includes every non-local column from the backing
//     SQLModel, including nulls and empty lists, minus query_policies
//     (and minus password for user records)
public class QueryResponseShapeTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public QueryResponseShapeTests(DmartFactory factory) => _factory = factory;

    private QueryService Resolve()
    {
        _factory.CreateClient();
        return _factory.Services.GetRequiredService<QueryService>();
    }

    // ==================== envelope ====================

    [Fact]
    public async Task Query_Response_Attributes_Has_Both_Total_And_Returned()
    {
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 100,
        }, _factory.AdminShortname);

        resp.Status.ShouldBe(Status.Success);
        resp.Attributes.ShouldNotBeNull();
        resp.Attributes!.ShouldContainKey("total");
        resp.Attributes.ShouldContainKey("returned");
        // returned MUST equal the actual page count
        resp.Attributes["returned"].ShouldBe(resp.Records!.Count);
    }

    [Fact]
    public async Task Query_Response_Total_Reflects_PreLimit_Count_Not_Page_Count()
    {
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        // Ask for a limit of 1 against a subpath we know has multiple rows
        // (management/ — always has entries there per the head-to-head
        // run). If `total` is the page count instead of the true count, this
        // assertion fails and flags the regression.
        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = "management",
            Subpath = "/",
            Limit = 1,
        }, _factory.AdminShortname);

        resp.Status.ShouldBe(Status.Success);
        var total = (int)resp.Attributes!["total"]!;
        var returned = (int)resp.Attributes["returned"]!;
        returned.ShouldBe(1);
        total.ShouldBeGreaterThan(returned, "total must be the pre-limit count, not the page count");
    }

    // ==================== Record envelope ====================

    [Fact]
    public async Task Record_Omits_Null_Attachments()
    {
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 1,
        }, _factory.AdminShortname);

        var rec = resp.Records!.First();
        // The Attachments property is null by default; we check it's still null
        // (serializer suppresses it with WhenWritingNull).
        rec.Attachments.ShouldBeNull();
    }

    // ==================== Record.attributes content ====================

    [Fact]
    public async Task Space_Record_Attributes_Has_Python_Parity_Key_Set()
    {
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 100,
        }, _factory.AdminShortname);

        var mgmt = resp.Records!.First(r => r.Shortname == "management");
        var keys = (mgmt.Attributes ?? new()).Keys;

        // Keys that are always present (non-null) for any Space row.
        // Null-valued keys are stripped from the response to match Python behaviour.
        var alwaysPresent = new[]
        {
            "is_active", "tags", "created_at", "updated_at", "owner_shortname",
            "acl", "relationships", "indexing_enabled", "capture_misses",
            "check_health", "languages", "space_name",
        };
        foreach (var e in alwaysPresent) keys.ShouldContain(e);

        // And verify query_policies is stripped (Python deletes it before
        // returning).
        keys.ShouldNotContain("query_policies");
    }

    [Fact]
    public async Task Entry_Record_Attributes_Has_Python_Parity_Key_Set()
    {
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = "management",
            Subpath = "/",
            Limit = 1,
        }, _factory.AdminShortname);

        resp.Records!.ShouldNotBeEmpty();
        var rec = resp.Records![0];
        var keys = (rec.Attributes ?? new()).Keys;

        // Keys that are always present (non-null) for any Entry row.
        // Null-valued keys are stripped from the response.
        var alwaysPresent = new[]
        {
            "is_active", "tags", "created_at", "updated_at",
            "owner_shortname", "acl", "relationships", "space_name",
        };
        foreach (var e in alwaysPresent) keys.ShouldContain(e);

        keys.ShouldNotContain("query_policies");
    }

    // ==================== retrieve_total semantics ====================

    [Fact]
    public async Task RetrieveTotal_Defaults_To_True_When_Null()
    {
        // A Query with RetrieveTotal=null should behave like Python's default
        // of true — we should see a real pre-limit count in attributes.total.
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = "management",
            Subpath = "/",
            Limit = 1,
            // RetrieveTotal left as null
        }, _factory.AdminShortname);

        var total = (int)resp.Attributes!["total"]!;
        total.ShouldBeGreaterThanOrEqualTo(1);
        total.ShouldNotBe(-1, "null RetrieveTotal must be treated as default true");
    }

    [Fact]
    public async Task RetrieveTotal_False_Skips_Count_And_Returns_Minus_One()
    {
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = "management",
            Subpath = "/",
            Limit = 1,
            RetrieveTotal = false,
        }, _factory.AdminShortname);

        // Explicit opt-out: Python returns total=-1 when the caller skips the
        // count query, and we match that sentinel.
        ((int)resp.Attributes!["total"]!).ShouldBe(-1);
    }

    // ==================== null stripping ====================

    [Fact]
    public async Task Entry_Attributes_Have_No_Null_Values()
    {
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = "management",
            Subpath = "/",
            Limit = 5,
        }, _factory.AdminShortname);

        resp.Records.ShouldNotBeEmpty();
        foreach (var rec in resp.Records!)
        {
            if (rec.Attributes is null) continue;
            foreach (var (key, val) in rec.Attributes)
                val.ShouldNotBeNull($"record {rec.Shortname} has null attribute: {key}");
        }
    }

    [Fact]
    public async Task Space_Attributes_Have_No_Null_Values()
    {
        if (!DmartFactory.HasPg) return;
        var query = Resolve();

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 100,
        }, _factory.AdminShortname);

        resp.Records.ShouldNotBeEmpty();
        foreach (var rec in resp.Records!)
        {
            if (rec.Attributes is null) continue;
            foreach (var (key, val) in rec.Attributes)
                val.ShouldNotBeNull($"space {rec.Shortname} has null attribute: {key}");
        }
    }

    // ==================== password / query_policies hidden ====================

    [Fact]
    public async Task User_Entry_Endpoint_Hides_Password()
    {
        if (!DmartFactory.HasPg) return;
        var client = _factory.CreateClient();
        var raw = await client.PostAsync("/user/login",
            new StringContent($"{{\"shortname\":\"{_factory.AdminShortname}\",\"password\":\"{_factory.AdminPassword}\"}}",
                System.Text.Encoding.UTF8, "application/json"));
        var loginBody = await raw.Content.ReadAsStringAsync();
        var token = System.Text.Json.JsonDocument.Parse(loginBody).RootElement
            .GetProperty("records")[0].GetProperty("attributes")
            .GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/managed/entry/user/management/users/{_factory.AdminShortname}");
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldNotContain("\"password\"");
        body.ShouldNotContain("\"query_policies\"");
    }
}
