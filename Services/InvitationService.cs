using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Coordinates invitation minting: build the JWT, persist the lookup row,
// assemble the public invitation URL when INVITATION_LINK is configured,
// and attempt SMS delivery for SMS-channel invitations when the gateway is
// wired.
//
// Callers include:
//   * UserService.CreateAsync — auto-mints for new users whose email/msisdn
//     haven't been verified via OTP-on-create.
//   * PasswordResetHandler — admin endpoint that mints a fresh invitation on
//     demand for an existing user (Python /user/reset parity).
//
// The returned token is the full JWT string the caller presents on
// POST /user/login. We surface it directly in the HTTP response for admin
// copy/paste so invitations still work even without a mail/SMS gateway —
// Python's behaviour when `mock_smtp_api`/`mock_smpp_api` is true.
public sealed class InvitationService(
    InvitationJwt jwt,
    InvitationRepository repo,
    SmsSender sms,
    SmtpSender smtp,
    ShortLinkService shortLinks,
    LanguageLoader languages,
    ActivationTemplateLoader activationTemplates,
    IOptions<DmartSettings> settings,
    ILogger<InvitationService> log)
{
    // Defensive fallback only — used when both the user's locale AND the
    // embedded English JSON are missing `activation_subject`. The shipped
    // languages/english.json MUST carry the same string for this key;
    // a CI-time check would be nice (no harness in place yet), but until
    // then the constant + the JSON value need to be kept in sync manually.
    // Python parity: utils/generate_email.py::generate_subject("activation").
    private const string EnglishActivationSubjectFallback = "Welcome to our Platform!";

    public async Task<string?> MintAsync(User user, InvitationChannel channel, CancellationToken ct = default)
        => await MintAsync(user, channel, isReset: false, ct);

    // isReset=true selects the password-reset SMS template (Python parity:
    // languages[user.language]["reset_message"], used by /user/reset). The
    // email body is unchanged — Python uses the same activation template for
    // both invitation and reset flows.
    public async Task<string?> MintAsync(User user, InvitationChannel channel, bool isReset, CancellationToken ct = default)
    {
        string? identifier = channel == InvitationChannel.Email ? user.Email : user.Msisdn;
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        // Mint, persist, and attempt delivery. We catch *non-cancellation*
        // failures here so callers can rely on "MintAsync never throws for
        // gateway/db hiccups" — this is what the comment in
        // RequestHandler.CreateUserAsync references. Cancellation must
        // propagate so the request pipeline sees a normal abort.
        string? token = null;
        try
        {
            token = jwt.Mint(user.Shortname, channel);
            var channelWire = channel == InvitationChannel.Email ? "EMAIL" : "SMS";
            await repo.UpsertAsync(token, $"{channelWire}:{identifier}", ct);

            // Assemble the public invitation URL the Python CXB/admin UI expects.
            // Format mirrors Python's repository.py template:
            //   {invitation_link}/auth/invitation?invitation={token}&lang={lang}&user-type={type}
            var url = BuildInvitationUrl(user, token);

            // Python parity: api/managed/utils.py wraps the long invitation URL
            // through repository.url_shortner before placing it in SMS / email
            // bodies — keeps SMS within length limits and gives every recipient
            // a stable {appUrl}/managed/s/{token} redirect.
            var deliverableLink = await ShortenAsync(url, ct) ?? url ?? token;

            if (channel == InvitationChannel.Sms)
            {
                // Python parity: api/managed/utils.py::send_sms_email_invitation
                // sends the localized invitation_message string with {link}
                // substituted. LanguageLoader is the single source of truth —
                // it handles per-language lookup and the English fallback that
                // Python's `languages[user.language]` would otherwise KeyError on.
                var key = isReset ? "reset_message" : "invitation_message";
                var template = languages.Get(user.Language, key);
                string text;
                if (template is not null)
                {
                    text = template.Replace("{link}", deliverableLink);
                }
                else
                {
                    log.LogError("translation missing: languages[{Lang}][{Key}] — sending raw link",
                        user.Language, key);
                    text = deliverableLink;
                }
                var ok = await sms.SendAsync(identifier, text, ct);
                if (!ok)
                    log.LogWarning("invitation SMS for {Shortname} to {Msisdn} not delivered — returning token in response body",
                        user.Shortname, identifier);
            }
            else
            {
                // Activation email — the subject and both body parts are now
                // dynamic. Subject text is sourced from LanguageLoader so
                // operators can localize it via ~/.dmart/languages/<lang>.json
                // (English fallback when the user's locale has no entry).
                // Body parts are sourced from ActivationTemplateLoader:
                // embedded templates/ActivationEmailContent.{html,txt} by
                // default, each replaced independently when
                // ~/.dmart/ActivationEmailContent.{html,txt} exists. All
                // three flow through the same `{{var}}` substitution with the
                // same {name, msisdn, shortname, link} scope so operators
                // can reference any of those vars in any field. SmtpSender
                // composes a multipart/alternative message when both bodies
                // are non-empty and falls back to whichever side is present
                // otherwise — see Services/SmtpSender.cs.
                // LanguageLoader already falls back to English when the user's
                // locale lacks the key. The hardcoded constant below covers
                // the pathological "even English doesn't have it" case
                // (config bug, botched override). An empty Subject header
                // would otherwise reach the MTA and some servers flag
                // blank-subject mail as spam.
                var subjectSource = languages.Get(user.Language, "activation_subject")
                                    ?? EnglishActivationSubjectFallback;
                var subject = activationTemplates.RenderSubject(subjectSource, user, deliverableLink);
                var htmlBody = activationTemplates.RenderHtmlBody(user, deliverableLink);
                var textBody = activationTemplates.RenderTextBody(user, deliverableLink);
                var ok = await smtp.SendEmailAsync(identifier, subject, htmlBody, textBody, ct);
                if (!ok)
                    log.LogWarning("invitation email for {Shortname} to {Email} not delivered — returning token in response body",
                        user.Shortname, identifier);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "invitation mint/delivery failed for {Shortname} via {Channel}",
                user.Shortname, channel);
        }
        return token;
    }

    // Python parity: utils/repository.py::url_shortner mints a fresh uuid4()[:8]
    // token per call without de-duping by target URL — every resend allocates
    // a new short-link row pointing at the same long URL. We deliberately
    // match that behaviour so a single invitation token can be invalidated
    // (e.g. on resend) without affecting prior tokens. Returns null when the
    // long URL is null, AppUrl is unconfigured, or persistence fails — caller
    // falls back to the long URL (or raw JWT) so a degraded shortener never
    // blocks delivery.
    private async Task<string?> ShortenAsync(string? longUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(longUrl)) return null;
        var appUrl = settings.Value.AppUrl;
        if (string.IsNullOrWhiteSpace(appUrl)) return null;
        try
        {
            var token = await shortLinks.CreateAsync(longUrl, ct);
            return $"{appUrl.TrimEnd('/')}/managed/s/{token}";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "url_shortner failed; falling back to long invitation URL");
            return null;
        }
    }

    // Null when INVITATION_LINK isn't configured — caller falls back to the
    // raw JWT, which still logs in via POST /user/login invitation path.
    private string? BuildInvitationUrl(User user, string token)
    {
        var baseUrl = settings.Value.InvitationLink;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var lang = JsonbHelpers.EnumMember(user.Language);
        var type = JsonbHelpers.EnumMember(user.Type);
        return $"{baseUrl.TrimEnd('/')}/auth/invitation?invitation={Uri.EscapeDataString(token)}&lang={lang}&user-type={type}";
    }

}
