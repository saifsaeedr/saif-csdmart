using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Services;

// Coordinates invitation minting: build the JWT, persist the lookup row, and
// log a warning that delivery is not yet implemented in the C# port.
//
// Callers include:
//   * UserService.CreateAsync — auto-mints for new users whose email/msisdn
//     haven't been verified via OTP-on-create.
//   * PasswordResetHandler — admin endpoint that mints a fresh invitation on
//     demand for an existing user (Python /user/reset parity).
//
// The returned token is the full JWT string the caller presents on
// POST /user/login. In this port we surface it directly in the HTTP
// response for admin copy/paste; Python instead transmits it over SMS/email.
public sealed class InvitationService(
    InvitationJwt jwt,
    InvitationRepository repo,
    ILogger<InvitationService> log)
{
    public async Task<string?> MintAsync(User user, InvitationChannel channel, CancellationToken ct = default)
    {
        string? identifier = channel == InvitationChannel.Email ? user.Email : user.Msisdn;
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        var token = jwt.Mint(user.Shortname, channel);
        var channelWire = channel == InvitationChannel.Email ? "EMAIL" : "SMS";
        await repo.UpsertAsync(token, $"{channelWire}:{identifier}", ct);

        // Delivery is the caller's responsibility in Python (SMTP/SMPP plugins).
        // The C# port has neither yet — log once per mint and rely on the
        // admin-facing response body to surface the token.
        log.LogWarning(
            "invitation minted for {Shortname} ({Channel}) — delivery is not implemented in the C# port; returned in HTTP response only",
            user.Shortname, channelWire);
        return token;
    }
}
