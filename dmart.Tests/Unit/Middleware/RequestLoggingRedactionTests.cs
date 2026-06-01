using Dmart.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Middleware;

// Security: the access log must never capture credentials. OAuth callbacks
// (Apple web) put a session JWT in the redirect URL → the Location response
// header; provider authorization `code`s arrive as request query params.
// Both must be masked. Pins RequestLoggingMiddleware's redaction.
public class RequestLoggingRedactionTests
{
    [Fact]
    public void Location_Header_Is_Redacted()
    {
        var headers = new HeaderDictionary
        {
            ["Location"] = "https://app.example/cb?access_token=eyJ.secret.sig",
            ["Content-Type"] = "text/html",
        };
        var red = RequestLoggingMiddleware.RedactHeaders(headers);
        red["Location"].ShouldBe("******");
        red["Content-Type"].ShouldBe("text/html");
    }

    [Fact]
    public void Sensitive_Query_Params_Are_Redacted_But_Benign_Ones_Pass()
    {
        var q = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["code"] = "oauth-authcode",
            ["access_token"] = "eyJ.secret",
            ["limit"] = "10",
        });
        var red = RequestLoggingMiddleware.RedactQueryParams(q);
        red["code"].ShouldBe("******");
        red["access_token"].ShouldBe("******");
        red["limit"].ShouldBe("10");
    }

    [Fact]
    public void Redacted_Query_String_Masks_Secrets_Only()
    {
        var q = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["code"] = "secret",
            ["state"] = "xyz",
        });
        var s = RequestLoggingMiddleware.RedactedQueryString(q);
        s.ShouldContain("code=******");
        s.ShouldContain("state=xyz");
    }
}
