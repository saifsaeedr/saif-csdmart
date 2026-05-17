using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

public sealed class ShortLinkService(LinkRepository links, IOptions<DmartSettings> settings)
{
    public async Task<string?> ResolveAsync(string token, CancellationToken ct = default)
    {
        var url = await links.ResolveAsync(token, ct);
        if (url is null) return null;

        // The resolver runs anonymously (ShortLinkHandler.cs:21 .AllowAnonymous),
        // so it must refuse to redirect anywhere that wasn't written by a
        // server-controlled path. Two such writers exist:
        //   * ShortLinkHandler /managed/shortening/... stores {AppUrl}/managed/entry/...
        //   * InvitationService.ShortenAsync stores {InvitationLink}/auth/invitation?...
        // Allow either; reject everything else so the public endpoint can
        // never become an open redirect.
        var s = settings.Value;
        if (string.IsNullOrWhiteSpace(s.AppUrl) && string.IsNullOrWhiteSpace(s.InvitationLink))
            return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var stored)) return null;
        if (MatchesBase(stored, s.AppUrl) || MatchesBase(stored, s.InvitationLink))
            return url;
        return null;
    }

    private static bool MatchesBase(Uri stored, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)) return false;
        return string.Equals(stored.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(stored.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && stored.Port == b.Port;
    }

    public Task<string> CreateAsync(string targetUrl, CancellationToken ct = default)
        => links.CreateAsync(targetUrl, ct);

    public async Task CreateAsync(string token, string targetUrl, TimeSpan expires, CancellationToken ct = default)
    {
        await links.CreateWithTokenAsync(token, targetUrl, TimeUtils.Now().Add(expires), ct);
    }
}
