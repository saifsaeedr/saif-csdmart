using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the error-code envelope that /user/create returns to match
// Python dmart byte-for-byte. The Python handler in
// dmart_plain/backend/api/user/router.py::create_user uses code=50
// (SESSION) with type="create" for every sync validation failure, plus
// code=415 (DATA_SHOULD_BE_UNIQUE) with type="request" for email/msisdn
// collisions (via validate_uniqueness), and code=400
// (SHORTNAME_ALREADY_EXIST) with type="create" for shortname collisions
// (via db.create).
public class UserCreateErrorCodesTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserCreateErrorCodesTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Missing_Email_And_Msisdn_Returns_Code_50_Type_Create()
    {
        var client = _factory.CreateClient();
        var body = "{\"resource_type\":\"user\",\"shortname\":\"noid_x\",\"subpath\":\"/\",\"attributes\":{\"password\":\"Testtest1234\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Failed);
        result.Error!.Code.ShouldBe(InternalErrorCode.SESSION);       // 50
        result.Error.Type.ShouldBe(ErrorTypes.Create);
    }

    [FactIfPg]
    public async Task Missing_Email_Otp_Returns_Code_50_Type_Create()
    {
        var client = _factory.CreateClient();
        var shortname = "otpmiss_" + Guid.NewGuid().ToString("N")[..6];
        var body = "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"" + shortname + "@x.y\",\"password\":\"Testtest1234\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Failed);
        result.Error!.Code.ShouldBe(InternalErrorCode.SESSION);       // 50
        result.Error.Type.ShouldBe(ErrorTypes.Create);
    }

    [FactIfPg]
    public async Task Weak_Password_Returns_Code_50_Type_Create()
    {
        var client = _factory.CreateClient();
        var shortname = "weakpw_" + Guid.NewGuid().ToString("N")[..6];
        // No digits / uppercase → fails the password regex.
        var body = "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"" + shortname + "@x.y\",\"password\":\"password\",\"email_otp\":\"000000\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Failed);
        result.Error!.Code.ShouldBe(InternalErrorCode.SESSION);       // 50
        result.Error.Type.ShouldBe(ErrorTypes.Create);
    }

    [FactIfPg]
    public async Task Duplicate_Shortname_Returns_Code_400_Type_Create()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.IsOtpForCreateRequired = false)));
        var client = factory.CreateClient();

        var shortname = "dup_" + Guid.NewGuid().ToString("N")[..6];
        var body1 = "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"" + shortname + "@x.y\",\"password\":\"Testtest1234\"}}";
        var first = await client.PostAsync("/user/create",
            new StringContent(body1, Encoding.UTF8, "application/json"));
        first.IsSuccessStatusCode.ShouldBeTrue("first create should succeed as seed for the conflict");

        // Second create: same shortname, different email → must hit the
        // shortname branch with code=SHORTNAME_ALREADY_EXIST, type=create.
        var body2 = "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"dup2_" + shortname + "@x.y\",\"password\":\"Testtest1234\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(body2, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Failed);
        result.Error!.Code.ShouldBe(InternalErrorCode.SHORTNAME_ALREADY_EXIST);  // 400
        result.Error.Type.ShouldBe(ErrorTypes.Create);

        // The first /user/create succeeded → resource_folders_creation
        // materialized people/{shortname}/* under "personal" owned by this
        // user. Purge those entries before deleting the user so the FK holds.
        await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
    }

    [FactIfPg]
    public async Task Duplicate_Email_Returns_Code_415_Type_Request()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.IsOtpForCreateRequired = false)));
        var client = factory.CreateClient();

        var shared = "share_" + Guid.NewGuid().ToString("N")[..6] + "@x.y";
        var first = "{\"resource_type\":\"user\",\"shortname\":\"em1_" + Guid.NewGuid().ToString("N")[..6] + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"" + shared + "\",\"password\":\"Testtest1234\"}}";
        var firstResp = await client.PostAsync("/user/create",
            new StringContent(first, Encoding.UTF8, "application/json"));
        firstResp.IsSuccessStatusCode.ShouldBeTrue();

        // Second user reuses the email — Python's validate_uniqueness path
        // raises 415/request with "Entry properties should be unique…".
        var second = "{\"resource_type\":\"user\",\"shortname\":\"em2_" + Guid.NewGuid().ToString("N")[..6] + "\",\"subpath\":\"/\",\"attributes\":{\"email\":\"" + shared + "\",\"password\":\"Testtest1234\"}}";
        var resp = await client.PostAsync("/user/create",
            new StringContent(second, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Failed);
        result.Error!.Code.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);  // 415
        result.Error.Type.ShouldBe(ErrorTypes.Request);
        result.Error.Message.ShouldContain("@email:");

        // First create succeeded → cleanup must purge user-owned folders
        // before the user row, see TestUserCleanup.
        var firstShortname = JsonSerializer.Deserialize(first, DmartJsonContext.Default.Record)!.Shortname;
        await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, firstShortname);
    }
}
