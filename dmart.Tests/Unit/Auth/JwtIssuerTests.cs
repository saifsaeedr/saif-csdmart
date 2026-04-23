using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.Models.Enums;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

public class JwtIssuerTests
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
    public void IssueAccess_Returns_Three_Segments()
    {
        var token = NewIssuer().IssueAccess("alice");
        token.Split('.').Length.ShouldBe(3);
    }

    [Fact]
    public void IssueAccess_Payload_Contains_Subject_Issuer_Audience_Exp()
    {
        var token = NewIssuer().IssueAccess("alice", new[] { "super_admin" });
        var parts = token.Split('.');
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        root.GetProperty("sub").GetString().ShouldBe("alice");
        root.GetProperty("iss").GetString().ShouldBe("dmart");
        root.GetProperty("aud").GetString().ShouldBe("dmart");
        root.TryGetProperty("exp", out _).ShouldBeTrue();
        root.TryGetProperty("iat", out _).ShouldBeTrue();
        var roles = root.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).ToArray();
        roles.ShouldContain("super_admin");
    }

    [Fact]
    public void IssueRefresh_Returns_Distinct_Token()
    {
        var jwt = NewIssuer();
        var access = jwt.IssueAccess("alice");
        var refresh = jwt.IssueRefresh("alice");
        access.ShouldNotBe(refresh);
    }

    [Fact]
    public void Two_Tokens_For_Same_Subject_Have_Different_Jti()
    {
        var jwt = NewIssuer();
        var a = jwt.IssueAccess("alice");
        var b = jwt.IssueAccess("alice");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Payload_Contains_Python_Compatible_Data_And_Expires()
    {
        var token = NewIssuer().IssueAccess("alice", new[] { "admin" });
        var root = DecodePayload(token);
        // Python dmart puts { "data": { "shortname": "...", "type": "..." }, "expires": N }
        root.TryGetProperty("data", out var data).ShouldBeTrue("missing data object");
        data.GetProperty("shortname").GetString().ShouldBe("alice");
        data.GetProperty("type").GetString().ShouldBe("web");
        root.TryGetProperty("expires", out var expires).ShouldBeTrue("missing expires");
        expires.GetInt64().ShouldBe(root.GetProperty("exp").GetInt64(),
            "expires should equal exp");
    }

    [Fact]
    public void Data_Type_Reflects_UserType_Parameter()
    {
        var token = NewIssuer().IssueAccess("bob", null, UserType.Mobile);
        var root = DecodePayload(token);
        root.GetProperty("data").GetProperty("type").GetString().ShouldBe("mobile");
    }

    [Fact]
    public void Refresh_Token_Also_Contains_Python_Data()
    {
        var token = NewIssuer().IssueRefresh("carol", UserType.Bot);
        var root = DecodePayload(token);
        root.GetProperty("data").GetProperty("shortname").GetString().ShouldBe("carol");
        root.GetProperty("data").GetProperty("type").GetString().ShouldBe("bot");
        root.TryGetProperty("expires", out _).ShouldBeTrue();
    }

    [Fact]
    public void Validate_Accepts_Token_With_New_Claims()
    {
        var issuer = NewIssuer();
        var token = issuer.IssueAccess("alice", new[] { "admin" }, UserType.Web);
        var principal = issuer.Validate(token);
        principal.ShouldNotBeNull();
        principal!.Identity!.Name.ShouldBe("alice");
    }

    private static JsonElement DecodePayload(string token)
    {
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(token.Split('.')[1]));
        return JsonDocument.Parse(payloadJson).RootElement;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
