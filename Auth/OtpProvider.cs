using System.Security.Cryptography;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

public sealed class OtpProvider(
    IOptions<DmartSettings> settings,
    SmsSender sms,
    SmtpSender smtp,
    LanguageLoader languages,
    ILogger<OtpProvider> log)
{
    public string Generate(string destination)
    {
        // Per-channel mock: only short-circuit when the channel that will
        // actually deliver this code is mocked. A half-mocked setup (e.g.
        // MockSmtpApi=true with a real SMS gateway) must still mint real
        // random codes for the live channel.
        var s = settings.Value;
        if (IsMsisdn(destination) && s.MockSmppApi)
        {
            log.LogWarning("OTP SMS mock active — returning configured MockOtpCode");
            return s.MockOtpCode;
        }
        if (IsEmail(destination) && s.MockSmtpApi)
        {
            log.LogWarning("OTP SMTP mock active — returning configured MockOtpCode");
            return s.MockOtpCode;
        }
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    public async Task SendAsync(string destination, string code,
        Language language = Language.En, CancellationToken ct = default)
    {
        // Dispatch:
        //   msisdn-shaped destination → SEND_SMS_OTP_API (configured) or log.
        //   email-shaped destination  → SMTP gateway (configured) or log.
        //   anything else             → log only.
        var body = RenderMessage(language, code);
        if (IsMsisdn(destination))
        {
            var sent = await sms.SendOtpAsync(destination, body,
                language: JsonbHelpers.EnumMember(language), ct);
            if (sent) return;
        }
        else if (IsEmail(destination))
        {
            // Python parity: email_send_otp() — HTML body containing the code.
            // Wrap the localized template in the same HTML shell Python uses,
            // so "Your OTP code is 123456" / "رمز التحقق…" both render bold.
            var html = $"<p>{System.Net.WebUtility.HtmlEncode(body)}</p>";
            var sent = await smtp.SendEmailAsync(destination, "OTP", html, ct);
            if (sent) return;
        }

        // Fallback: log so developers can retrieve the code from server logs.
        log.LogInformation("OTP for {Destination}: {Code} (delivery not implemented or gateway unavailable)",
            destination, code);
    }

    // Resolves `otp_message` from the loaded languages and substitutes the
    // `{code}` placeholder. Falls back to the historical English literal when
    // the key isn't loaded — mirrors Python's send_otp() which does the same
    // dictionary lookup with a hard-coded fallback. Operators override the
    // template by dropping a JSON file at ~/.dmart/languages/<lang>.json with
    // an `otp_message` key (LanguageLoader strategy 3).
    //
    // Internal so the unit suite can pin the rendering contract without
    // standing up the full SMS / SMTP send pipeline.
    internal string RenderMessage(Language language, string code)
    {
        const string fallback = "Your OTP code is {code}";
        var template = languages.Get(language, "otp_message") ?? fallback;
        return template.Replace("{code}", code, StringComparison.Ordinal);
    }

    // Lightweight email heuristic — good enough for dispatch routing; the OTP
    // flow validates the full address format upstream when the user registered.
    private static bool IsEmail(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        var at = destination.IndexOf('@');
        return at > 0 && at < destination.Length - 1 && destination.IndexOf('.', at) > at;
    }

    // Phone-number heuristic: +<digits> or pure digits of length 6+. Matches
    // Python's User.msisdn regex behaviour for typical E.164 inputs.
    private static bool IsMsisdn(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        var s = destination.StartsWith('+') ? destination[1..] : destination;
        if (s.Length < 6) return false;
        foreach (var c in s) if (!char.IsDigit(c)) return false;
        return true;
    }
}
