using System.Net.Http;
using System.Net.Http.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Security: /managed/import and /managed/export write/read arbitrary rows
// across EVERY space — the import service trusts each row's own
// owner_shortname and bypasses per-row ACLs. They must therefore be gated to
// GLOBAL admins (a permission spanning __all_spaces__), not merely
// authenticated users. Otherwise any logged-in (even self-registered,
// role-less) user could import management/users|roles|permissions rows and
// escalate to super_admin.
public class ImportAuthzTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ImportAuthzTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Import_By_NonAdmin_Is_Rejected_NOT_ALLOWED()
    {
        // A logged-in user with NO roles — the privilege level of a fresh
        // self-registration.
        var user = await _factory.CreateLoggedInUserAsync(roles: new());
        try
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(System.Array.Empty<byte>()), "zip_file", "x.zip" },
            };
            var resp = await user.Client.PostAsync("/managed/import", form);
            var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            result!.Status.ShouldBe(Status.Failed);
            result.Error!.Code.ShouldBe(InternalErrorCode.NOT_ALLOWED);
        }
        finally { await user.Cleanup(); }
    }

    [FactIfPg]
    public async Task Export_By_NonAdmin_Is_Rejected_NOT_ALLOWED()
    {
        var user = await _factory.CreateLoggedInUserAsync(roles: new());
        try
        {
            var resp = await user.Client.PostAsJsonAsync("/managed/export",
                new Query { Type = QueryType.Subpath, SpaceName = "management", Subpath = "/users" },
                DmartJsonContext.Default.Query);
            // Must NOT stream a zip of another space's data.
            resp.Content.Headers.ContentType?.MediaType.ShouldNotBe("application/zip");
            var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            result!.Status.ShouldBe(Status.Failed);
            result.Error!.Code.ShouldBe(InternalErrorCode.NOT_ALLOWED);
        }
        finally { await user.Cleanup(); }
    }

    [FactIfPg]
    public async Task Import_By_Admin_Passes_Authorization()
    {
        // super_admin → global admin → authz passes; the request then fails on
        // the missing zip payload (MISSING_DATA), proving we got PAST the gate.
        var admin = await _factory.CreateLoggedInUserAsync(roles: new() { "super_admin" });
        try
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent("x"), "extra" }, // no zip_file part
            };
            var resp = await admin.Client.PostAsync("/managed/import", form);
            var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            result!.Error!.Code.ShouldBe(InternalErrorCode.MISSING_DATA);
        }
        finally { await admin.Cleanup(); }
    }
}
