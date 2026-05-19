using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the error-classification behavior of the global PG exception handler
// (Program.cs:WriteDbFailureAsync / WriteRequestFailureAsync). When PG
// rejects a query because the user's search expression references an
// unknown column (SqlState 42703), the response must classify as
// user-side (type=request / INVALID_DATA / HTTP 400) — not server-side
// (type=db / SOMETHING_WRONG / HTTP 500). Clients branching on
// `error.type` rely on this distinction.
public sealed class QueryErrorClassificationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public QueryErrorClassificationTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Query_With_Unknown_Search_Field_Returns_Request_Error_Not_Db_Error()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        try
        {
            // Reference a search field that isn't a real column on entries —
            // SearchExpressionParser only validates syntax, so PG receives
            // SELECT ... WHERE asd = ... and rejects with 42703.
            var body = new StringContent("""
            {
              "type": "search",
              "space_name": "management",
              "subpath": "/users",
              "search": "@asd:123",
              "limit": 10
            }
            """, Encoding.UTF8, "application/json");

            var resp = await user.Client.PostAsync("/managed/query", body);

            // The whole point of this PR — user typo should map to 400, not 500.
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            var payload = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            payload.ShouldNotBeNull();
            payload!.Status.ShouldBe(Status.Failed);
            payload.Error.ShouldNotBeNull();
            payload.Error!.Type.ShouldBe(ErrorTypes.Request);
            payload.Error.Code.ShouldBe(InternalErrorCode.INVALID_DATA);

            // The message names the offending field (extracted from PG's
            // English MessageText) AND points at the @payload.body.<field>
            // form that actually works for custom payload fields.
            payload.Error.Message.ShouldContain("asd");
            payload.Error.Message.ShouldContain("@payload.body.asd");

            // The info envelope carries the request's correlation id for
            // server-log correlation, but must NOT carry PG metadata
            // (sqlstate / constraint / table / column) that the
            // earlier-PR redaction policy keeps out of the wire.
            payload.Error.Info.ShouldNotBeNull();
            payload.Error.Info!.Count.ShouldBe(1);
            payload.Error.Info[0].Keys.ShouldContain("cid");
            payload.Error.Info[0].Keys.ShouldNotContain("sqlstate");
            payload.Error.Info[0].Keys.ShouldNotContain("table");
            payload.Error.Info[0].Keys.ShouldNotContain("constraint");
        }
        finally
        {
            await user.Cleanup();
        }
    }
}
