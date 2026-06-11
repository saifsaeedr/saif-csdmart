using System.Net.Http.Json;
using System.Text;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the user-type policy for POST /user/create (self-registration):
//   * "web" and "mobile" are honored — Python parity (core.User.from_record
//     accepted caller-supplied type);
//   * "bot" is deliberately coerced to web — bot tokens bypass session
//     validation/eviction, so the public endpoint must not mint them;
//   * anything else (junk, numeric enum strings like "7", omitted) lands on
//     web and the request still succeeds — never a 500 from the PG usertype
//     enum rejecting an undefined value.
// The managed/admin create path still assigns bot for authorized admins.
public class UserCreateTypeTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserCreateTypeTests(DmartFactory factory) => _factory = factory;

    private static StringContent CreateBody(string email, string? type) => new(
        "{\"attributes\":{\"email\":\"" + email + "\",\"password\":\"Testtest1234\""
        + (type is null ? "" : ",\"type\":\"" + type + "\"")
        + "}}",
        Encoding.UTF8, "application/json");

    private HttpClient NoOtpClient() => _factory.WithWebHostBuilder(b =>
        b.ConfigureServices(svcs => svcs.Configure<DmartSettings>(s =>
            s.IsOtpForCreateRequired = false))).CreateClient();

    [FactIfPg]
    public async Task Create_Honors_Mobile_Type()
    {
        var client = NoOtpClient();
        var email = "typemob_" + Guid.NewGuid().ToString("N")[..6] + "@x.y";

        var resp = await client.PostAsync("/user/create", CreateBody(email, "mobile"));
        resp.IsSuccessStatusCode.ShouldBeTrue();
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var shortname = result!.Records![0].Shortname;

        var users = _factory.Services.GetRequiredService<UserRepository>();
        var created = await users.GetByShortnameAsync(shortname);
        created.ShouldNotBeNull();
        created!.Type.ShouldBe(UserType.Mobile);

        await TestUserCleanup.DeleteUserAndOwnedAsync(_factory.Services, shortname);
    }

    [TheoryIfPg]
    [InlineData("bot")]      // privileged type — silently coerced
    [InlineData("7")]        // numeric enum string — must not reach the DB as an undefined value
    [InlineData("garbage")]  // unknown string
    [InlineData(null)]       // omitted entirely
    public async Task Create_Coerces_Everything_Else_To_Web(string? type)
    {
        var client = NoOtpClient();
        var email = "typeweb_" + Guid.NewGuid().ToString("N")[..6] + "@x.y";

        var resp = await client.PostAsync("/user/create", CreateBody(email, type));
        resp.IsSuccessStatusCode.ShouldBeTrue(
            $"type={type ?? "<omitted>"} must register fine, got {(int)resp.StatusCode}");
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var shortname = result!.Records![0].Shortname;

        var users = _factory.Services.GetRequiredService<UserRepository>();
        var created = await users.GetByShortnameAsync(shortname);
        created.ShouldNotBeNull();
        created!.Type.ShouldBe(UserType.Web);

        await TestUserCleanup.DeleteUserAndOwnedAsync(_factory.Services, shortname);
    }
}
