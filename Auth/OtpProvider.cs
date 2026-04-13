using System.Security.Cryptography;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

public sealed class OtpProvider(IOptions<DmartSettings> settings, ILogger<OtpProvider> log)
{
    public string Generate()
    {
        // In mock mode, return the configured mock code (for dev/testing).
        var s = settings.Value;
        if (s.MockSmtpApi || s.MockSmppApi)
        {
            log.LogWarning("OTP mock mode active — returning configured MockOtpCode");
            return s.MockOtpCode;
        }
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    public Task SendAsync(string destination, string code, CancellationToken ct = default)
    {
        // TODO: hook SMS / email gateway (Twilio, AWS SNS, SMTP, etc.)
        // For now, log the code so developers can retrieve it from server logs.
        log.LogInformation("OTP for {Destination}: {Code} (delivery not implemented — check server logs)",
            destination, code);
        return Task.CompletedTask;
    }
}
