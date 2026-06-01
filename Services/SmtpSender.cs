using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Thin wrapper around System.Net.Mail.SmtpClient (AOT-safe — no reflection).
// Mirrors Python's aiosmtplib send_email() path in backend/api/user/service.py:
// delivers a message to the caller's email, returns false on any error so
// the caller can fall back to on-server logging the same way the SmsSender
// degrades when the SMS gateway isn't configured.
//
// Two overloads:
//   * SendEmailAsync(to, subject, htmlBody, ct) — legacy single-body path
//     used by OtpProvider and the plugin-callback path. Stays HTML-only.
//   * SendEmailAsync(to, subject, htmlBody, textBody, ct) — multipart/alternative
//     send: emits both parts when non-empty; degrades to whichever side is
//     present otherwise; refuses the send when both are empty.
public sealed class SmtpSender(IOptions<DmartSettings> settings, ILogger<SmtpSender> log)
{
    public Task<bool> SendEmailAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        => SendEmailAsync(to, subject, htmlBody, textBody: string.Empty, ct);

    public async Task<bool> SendEmailAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        var s = settings.Value;
        if (s.MockSmtpApi)
        {
            log.LogWarning("MOCK_SMTP_API=true — not sending to {To}: {Subject}", to, subject);
            return true;
        }
        if (string.IsNullOrWhiteSpace(s.MailHost))
        {
            log.LogWarning("SMTP gateway not configured (MailHost blank) — dropping message to {To}", to);
            return false;
        }
        // Empty bodies are spam-filter bait. If a template rendering upstream
        // degraded to "" — drop the send so the caller's existing "email not
        // delivered → fall back" branch engages instead of a blank email
        // landing in the recipient's spam folder.
        var hasHtml = !string.IsNullOrWhiteSpace(htmlBody);
        var hasText = !string.IsNullOrWhiteSpace(textBody);
        if (!hasHtml && !hasText)
        {
            log.LogWarning("empty email body — refusing to send blank message to {To} (subject: {Subject})", to, subject);
            return false;
        }

        try
        {
            using var client = new SmtpClient(s.MailHost, s.MailPort)
            {
                EnableSsl = s.MailUseTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15_000,
            };
            if (!string.IsNullOrWhiteSpace(s.MailUsername))
                client.Credentials = new NetworkCredential(s.MailUsername, s.MailPassword);

            var fromName = string.IsNullOrWhiteSpace(s.MailFromName) ? s.MailFromAddress : s.MailFromName;
            using var msg = new MailMessage
            {
                From = new MailAddress(s.MailFromAddress, fromName),
                Subject = subject,
            };
            msg.To.Add(to);

            if (hasHtml && hasText)
            {
                // RFC 2046 §5.1.4: in multipart/alternative the LAST part is
                // the most faithful representation. MailMessage.Body becomes
                // the first part (text/plain) and the alternate view is
                // appended (text/html). HTML-capable clients render HTML;
                // text-only clients fall back to the plain part cleanly.
                msg.Body = textBody;
                msg.IsBodyHtml = false;
                msg.AlternateViews.Add(
                    AlternateView.CreateAlternateViewFromString(htmlBody, null, MediaTypeNames.Text.Html));
            }
            else if (hasHtml)
            {
                msg.Body = htmlBody;
                msg.IsBodyHtml = true;
            }
            else
            {
                msg.Body = textBody;
                msg.IsBodyHtml = false;
            }

            await client.SendMailAsync(msg, ct);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SMTP send to {To} failed", to);
            return false;
        }
    }
}
