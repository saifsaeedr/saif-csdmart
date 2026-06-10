using System.Text;
using Dmart.Auth;
using Dmart.Config;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

public class TokenUseReadTests
{
    private static JwtIssuer NewIssuer() => new(Options.Create(new DmartSettings
    {
        JwtSecret = "test-secret-test-secret-test-secret-32",
        JwtIssuer = "dmart",
        JwtAudience = "dmart",
        JwtAccessExpires = 300,
        JwtRefreshDays = 1,
    }));

    [Fact]
    public void Read_Returns_Access_For_Access_Token()
        => TokenUse.Read(NewIssuer().IssueAccess("alice")).ShouldBe("access");

    [Fact]
    public void Read_Returns_Refresh_For_Refresh_Token()
        => TokenUse.Read(NewIssuer().IssueRefresh("alice")).ShouldBe("refresh");

    [Fact]
    public void Read_Returns_Null_For_Claimless_Token()
    {
        // Hand-rolled token without token_use (pre-2026-06 / Python-style).
        var payload = """{"sub":"alice","exp":9999999999}""";
        const string headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var header = JwtIssuer.Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var body = JwtIssuer.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        TokenUse.Read($"{header}.{body}.sig").ShouldBeNull();
    }

    [Fact]
    public void Read_Returns_Null_For_Garbage()
        => TokenUse.Read("not-a-jwt").ShouldBeNull();
}
