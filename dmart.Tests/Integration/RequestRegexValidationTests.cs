using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Python-parity regex validation for /managed/request payloads.
//
// Mirrors Python's backend/utils/regex.py — every model field that's typed
// `str = Field(pattern=regex.X)` in core.py / api.py gets the same anchored
// regex enforcement here. Without this, the C# port silently accepts
// shortnames Python would reject (control chars, slashes, oversize, etc.),
// which both violates parity and produced the "null byte → 500" finding from
// the security audit.
public sealed class RequestRegexValidationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;

    public RequestRegexValidationTests(DmartFactory factory) => _factory = factory;

    // ============================================================
    // 1. UNIT-LEVEL: pattern boundary tests
    // ============================================================

    [Theory]
    // Valid shortnames per Python SHORTNAME = ^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,64}$
    [InlineData("a")]                          // single char
    [InlineData("foo")]                        // ASCII letters
    [InlineData("foo_bar")]                    // underscore
    [InlineData("foo123")]                     // digits
    [InlineData("123foo")]                     // leading digits
    [InlineData("ABCdef")]                     // mixed case
    [InlineData("____")]                       // all underscores
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 64 chars max
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]                 // 48 chars
    [InlineData("علي")]                       // Arabic letters
    [InlineData("user_علي")]                  // mixed Latin + Arabic
    [InlineData("١٢٣")]                       // Arabic-Indic digits
    public void IsValidShortname_AcceptsPythonValid(string input)
    {
        RequestRegex.IsValidShortname(input).ShouldBeTrue($"'{input}' should match SHORTNAME pattern");
    }

    [Theory]
    [InlineData("")]                           // empty
    [InlineData("foo bar")]                    // space
    [InlineData("foo-bar")]                    // hyphen (Python: not in SHORTNAME, only SLUG)
    [InlineData("foo.bar")]                    // dot
    [InlineData("foo/bar")]                    // slash
    [InlineData("foo\tbar")]                   // tab
    [InlineData("foo\nbar")]                   // newline
    [InlineData("foo\0bar")]                   // null byte
    [InlineData("foo\"bar")]                   // double quote
    [InlineData("<script>")]                   // HTML
    [InlineData("../etc/passwd")]              // path traversal
    [InlineData("foo;DROP TABLE")]             // SQL-shaped
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars (over)
    public void IsValidShortname_RejectsPythonInvalid(string input)
    {
        RequestRegex.IsValidShortname(input).ShouldBeFalse($"'{input.Replace("\0", "\\0").Replace("\n", "\\n").Replace("\t", "\\t")}' should NOT match SHORTNAME");
    }

    [Theory]
    // SUBPATH = ^[a-zA-Zء-ي0-9٠-٩ً-ٟ_/]{1,128}$
    // (slash ALLOWED here, unlike SHORTNAME).
    [InlineData("/")]                          // root
    [InlineData("/users")]                     // single segment
    [InlineData("/users/profile")]             // multi-segment
    [InlineData("users")]                      // no leading slash
    [InlineData("a/b/c/d/e/f")]                // deep path
    [InlineData("a_b/c_d")]                    // underscore
    [InlineData("/علي/profile")]              // Arabic
    public void IsValidSubpath_AcceptsPythonValid(string input)
    {
        RequestRegex.IsValidSubpath(input).ShouldBeTrue($"'{input}' should match SUBPATH pattern");
    }

    [Theory]
    [InlineData("/foo bar")]                   // space
    [InlineData("/foo-bar")]                   // hyphen
    [InlineData("/foo.bar")]                   // dot
    [InlineData("/foo\tbar")]                  // tab
    [InlineData("/foo<script>")]               // HTML
    [InlineData("/foo;DROP")]                  // SQL-shaped
    // Null-byte is exercised via the HTTP integration test below; the Theory
    // can't easily distinguish a literal null from a space in its test ID,
    // so xUnit dedupes the InlineData rows.
    public void IsValidSubpath_RejectsPythonInvalid(string input)
    {
        RequestRegex.IsValidSubpath(input).ShouldBeFalse($"'{input.Replace("\0", "\\0").Replace("\t", "\\t")}' should NOT match SUBPATH");
    }

    [Theory]
    // SPACENAME = ^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,32}$
    [InlineData("management")]
    [InlineData("test")]
    [InlineData("acme_corp")]
    [InlineData("space123")]
    [InlineData("علي")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 32 chars max
    public void IsValidSpaceName_AcceptsPythonValid(string input)
    {
        RequestRegex.IsValidSpaceName(input).ShouldBeTrue($"'{input}' should match SPACENAME");
    }

    [Theory]
    [InlineData("")]
    [InlineData("space-name")]                 // hyphen
    [InlineData("space.name")]                 // dot
    [InlineData("space/name")]                 // slash
    [InlineData("space name")]                 // space
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33 chars (over)
    public void IsValidSpaceName_RejectsPythonInvalid(string input)
    {
        RequestRegex.IsValidSpaceName(input).ShouldBeFalse($"'{input}' should NOT match SPACENAME");
    }

    [Theory]
    // SLUG = ^[a-zA-Z0-9_-]{1,64}$ — hyphen IS allowed here.
    [InlineData("foo")]
    [InlineData("foo-bar")]
    [InlineData("foo_bar")]
    [InlineData("foo123")]
    [InlineData("FOO-bar_baz")]
    public void IsValidSlug_AcceptsPythonValid(string input)
    {
        RequestRegex.IsValidSlug(input).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo bar")]                    // space
    [InlineData("foo.bar")]                    // dot
    [InlineData("foo/bar")]                    // slash
    [InlineData("foo@bar")]                    // at
    [InlineData("علي")]                       // Arabic — slug is ASCII-only
    public void IsValidSlug_RejectsPythonInvalid(string input)
    {
        RequestRegex.IsValidSlug(input).ShouldBeFalse();
    }

    [Theory]
    // EMAIL = ^[a-z0-9_\.-]+@([a-z0-9_-]+\.)+[a-z0-9]{2,4}$
    [InlineData("user@example.com")]
    [InlineData("first.last@example.com")]
    [InlineData("with_underscore@example.co")]
    [InlineData("hyphen-in-name@example.io")]
    [InlineData("multi.dots@a.b.c.com")]
    public void IsValidEmail_AcceptsPythonValid(string input)
    {
        RequestRegex.IsValidEmail(input).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not_an_email")]
    [InlineData("@example.com")]               // no local part
    [InlineData("user@")]                       // no domain
    [InlineData("USER@example.com")]            // Python pattern is lowercase only
    [InlineData("user@example")]                // no TLD
    [InlineData("user@example.toolongtld")]     // TLD must be 2-4 chars per Python
    [InlineData("user name@example.com")]       // space in local
    public void IsValidEmail_RejectsPythonInvalid(string input)
    {
        RequestRegex.IsValidEmail(input).ShouldBeFalse();
    }

    [Theory]
    // MSISDN = ^[1-9][0-9]{9,14}$
    [InlineData("1234567890")]                 // 10 digits, leading 1
    [InlineData("123456789012345")]            // 15 digits max
    [InlineData("9999999999")]
    public void IsValidMsisdn_AcceptsPythonValid(string input)
    {
        RequestRegex.IsValidMsisdn(input).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789")]                 // leading zero
    [InlineData("123456789")]                  // 9 digits (under)
    [InlineData("1234567890123456")]           // 16 digits (over)
    [InlineData("12345abcde")]                 // letters
    [InlineData("+1234567890")]                // plus prefix
    public void IsValidMsisdn_RejectsPythonInvalid(string input)
    {
        RequestRegex.IsValidMsisdn(input).ShouldBeFalse();
    }

    [Theory]
    // OTP_CODE = ^[0-9٠-٩]{6}$ — exactly 6 digits.
    [InlineData("123456")]
    [InlineData("000000")]
    [InlineData("999999")]
    [InlineData("١٢٣٤٥٦")]                  // Arabic-Indic digits
    public void IsValidOtpCode_AcceptsPythonValid(string input)
    {
        RequestRegex.IsValidOtpCode(input).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]                      // 5 digits
    [InlineData("1234567")]                    // 7 digits
    [InlineData("12345a")]                     // letter
    [InlineData("12 456")]                     // space
    public void IsValidOtpCode_RejectsPythonInvalid(string input)
    {
        RequestRegex.IsValidOtpCode(input).ShouldBeFalse();
    }

    [Theory]
    // PASSWORD = ^(?=.*[0-9])(?=.*[A-Z])[chars]{8,64}$
    [InlineData("Password1")]                  // letters + digit
    [InlineData("Test1234")]
    [InlineData("Aaaaaaa1")]                   // exactly 8 chars
    [InlineData("Strong#Password!9")]          // special chars
    public void IsValidPassword_AcceptsPythonValid(string input)
    {
        RequestRegex.IsValidPassword(input).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("password1")]                  // no uppercase
    [InlineData("Password")]                   // no digit
    [InlineData("PassWord")]                   // no digit (different shape)
    [InlineData("Pass1")]                      // 5 chars (under 8)
    public void IsValidPassword_RejectsPythonInvalid(string input)
    {
        RequestRegex.IsValidPassword(input).ShouldBeFalse();
    }

    // ============================================================
    // 2. INTEGRATION: HTTP layer enforcement
    //    Confirm /managed/request returns 4xx (not 5xx) on invalid input.
    // ============================================================

    [FactIfPg]
    public async Task ManagedRequest_With_Invalid_Shortname_Returns_400_Not_500()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        try
        {
            var probes = new[]
            {
                "foo bar",      // space
                "foo\tbar",     // tab
                "foo bar",  // null byte (Finding #3 from pentest)
                "foo-bar",      // hyphen
                "foo.bar",      // dot
                "foo/bar",      // slash
                "<script>",     // HTML
                "../etc",       // path traversal
                new string('a', 65),  // 65 chars — over 64-max
            };
            foreach (var probe in probes)
            {
                var resp = await client.PostAsJsonAsync("/managed/request", new Request
                {
                    RequestType = RequestType.Create,
                    SpaceName = "test",
                    Records = new()
                    {
                        new() {
                            ResourceType = ResourceType.Content,
                            Subpath = "/itest",
                            Shortname = probe,
                            Attributes = new() { ["displayname"] = "regex probe" },
                        },
                    },
                }, DmartJsonContext.Default.Request);

                ((int)resp.StatusCode).ShouldBeLessThan(500,
                    $"invalid shortname probe must return 4xx, not 5xx. probe='{probe.Replace("\0", "\\0").Replace("\t", "\\t")}' got {(int)resp.StatusCode}");
                ((int)resp.StatusCode).ShouldBeGreaterThanOrEqualTo(400,
                    $"invalid shortname must NOT be accepted as 2xx. probe='{probe}'");
            }
        }
        finally { await cleanup(); }
    }

    [FactIfPg]
    public async Task ManagedRequest_With_Invalid_Subpath_Returns_400()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        try
        {
            // Subpath allows slashes, so attacks shift to other classes.
            var probes = new[]
            {
                "/foo bar",     // space
                "/foo\tbar",    // tab
                "/foo bar", // null byte
                "/foo.bar",     // dot
                "/foo-bar",     // hyphen
                "/<script>",    // HTML
                "/" + new string('a', 130),  // over 128-char SUBPATH limit
            };
            foreach (var probe in probes)
            {
                var resp = await client.PostAsJsonAsync("/managed/request", new Request
                {
                    RequestType = RequestType.Create,
                    SpaceName = "test",
                    Records = new()
                    {
                        new() {
                            ResourceType = ResourceType.Content,
                            Subpath = probe,
                            Shortname = "validname",
                            Attributes = new() { ["displayname"] = "subpath probe" },
                        },
                    },
                }, DmartJsonContext.Default.Request);

                ((int)resp.StatusCode).ShouldBeLessThan(500,
                    $"invalid subpath must return 4xx, not 5xx. probe='{probe.Replace("\0", "\\0").Replace("\t", "\\t")}' got {(int)resp.StatusCode}");
            }
        }
        finally { await cleanup(); }
    }

    [FactIfPg]
    public async Task ManagedRequest_With_Invalid_SpaceName_Returns_400()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        try
        {
            var probes = new[]
            {
                "test space",    // space
                "test/space",    // slash
                "test-space",    // hyphen (allowed in slug, NOT in spacename)
                "test.space",    // dot
                new string('a', 33),  // over 32-char SPACENAME limit
            };
            foreach (var probe in probes)
            {
                var resp = await client.PostAsJsonAsync("/managed/request", new Request
                {
                    RequestType = RequestType.Create,
                    SpaceName = probe,
                    Records = new()
                    {
                        new() {
                            ResourceType = ResourceType.Content,
                            Subpath = "/itest",
                            Shortname = "validname",
                            Attributes = new() { ["displayname"] = "spacename probe" },
                        },
                    },
                }, DmartJsonContext.Default.Request);

                ((int)resp.StatusCode).ShouldBeLessThan(500,
                    $"invalid space_name must return 4xx, not 5xx. probe='{probe}' got {(int)resp.StatusCode}");
            }
        }
        finally { await cleanup(); }
    }

    [FactIfPg]
    public async Task ManagedRequest_Error_Body_Identifies_Failing_Field()
    {
        // The error message should clearly say WHICH field failed and what
        // pattern it failed against — operators triaging 400s should not have
        // to guess.
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        try
        {
            var resp = await client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Create,
                SpaceName = "test",
                Records = new()
                {
                    new() {
                        ResourceType = ResourceType.Content,
                        Subpath = "/itest",
                        Shortname = "bad-name",  // hyphen not allowed
                        Attributes = new() { ["displayname"] = "named field probe" },
                    },
                },
            }, DmartJsonContext.Default.Request);

            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var raw = await resp.Content.ReadAsStringAsync();
            raw.Contains("shortname").ShouldBeTrue($"error body should name 'shortname'. Got: {raw}");
            raw.Contains("bad-name").ShouldBeTrue($"error body should echo the bad value. Got: {raw}");
        }
        finally { await cleanup(); }
    }

    [FactIfPg]
    public async Task ManagedRequest_With_Valid_Inputs_Still_Succeeds()
    {
        // Sanity: the regex gate must NOT reject legitimate inputs.
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        try
        {
            var resp = await client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Create,
                SpaceName = "test",
                Records = new()
                {
                    new() {
                        ResourceType = ResourceType.Content,
                        Subpath = "/itest",
                        Shortname = "valid_name_123",
                        Attributes = new() { ["displayname"] = "valid probe" },
                    },
                },
            }, DmartJsonContext.Default.Request);

            resp.StatusCode.ShouldBe(HttpStatusCode.OK,
                "valid inputs must still pass through the regex gate");

            // Cleanup the created entry.
            await client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = "test",
                Records = new()
                {
                    new() {
                        ResourceType = ResourceType.Content,
                        Subpath = "/itest",
                        Shortname = "valid_name_123",
                    },
                },
            }, DmartJsonContext.Default.Request);
        }
        finally { await cleanup(); }
    }
}
