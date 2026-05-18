using Dmart.Config;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

// SmtpSender is thin — it just wraps System.Net.Mail.SmtpClient. We can't unit
// test the real SMTP path without a test SMTP server, so these tests verify
// the degradation branches: mock mode returns true without sending, missing
// MailHost returns false without throwing. End-to-end SMTP delivery must be
// manually smoke-tested against a real SMTP server (aiosmtpd, mailhog).
public class SmtpSenderTests
{
    private static SmtpSender Build(DmartSettings s) =>
        new(Options.Create(s), NullLogger<SmtpSender>.Instance);

    [Fact]
    public async Task MockSmtpApi_True_ReturnsTrue_WithoutSending()
    {
        var sender = Build(new DmartSettings { MockSmtpApi = true });
        var ok = await sender.SendEmailAsync("anyone@example.com", "Test", "<p>hello</p>");
        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task MailHost_Empty_ReturnsFalse_WithoutThrowing()
    {
        var sender = Build(new DmartSettings { MailHost = "" });
        var ok = await sender.SendEmailAsync("anyone@example.com", "Test", "<p>hello</p>");
        ok.ShouldBeFalse();
    }

    // ActivationTemplateLoader (and any future template renderer) returns
    // empty string when its source template fails to parse or is missing.
    // SmtpSender must refuse to ship a blank-body email so the upstream
    // caller's "email not delivered → fall back to in-response token"
    // branch engages, instead of an empty message landing in spam.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n\t  ")]
    public async Task EmptyOrWhitespaceBody_ReturnsFalse_WithoutSending(string body)
    {
        // Non-empty MailHost so we reach the body-check (not the earlier
        // MailHost-blank guard). MockSmtpApi off so we exercise the real
        // path up to the connect attempt — which we must NOT reach.
        var sender = Build(new DmartSettings
        {
            MailHost = "127.0.0.1",
            MailPort = 2,           // closed; if we got here the test'd still pass
            MailUseTls = false,
            MailFromAddress = "sender@example.com",
        });
        var ok = await sender.SendEmailAsync("anyone@example.com", "Test", body);
        ok.ShouldBeFalse();
    }

    [Fact]
    public async Task MailHost_Unreachable_ReturnsFalse_WithoutThrowing()
    {
        // Port 2 on localhost is always closed — SmtpClient throws SocketException,
        // caught by SmtpSender and logged as error, returning false.
        var sender = Build(new DmartSettings
        {
            MailHost = "127.0.0.1",
            MailPort = 2,
            MailUseTls = false,
            MailFromAddress = "sender@example.com",
        });
        var ok = await sender.SendEmailAsync("anyone@example.com", "Test", "<p>hello</p>");
        ok.ShouldBeFalse();
    }
}
