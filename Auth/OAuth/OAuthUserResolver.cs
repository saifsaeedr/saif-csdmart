using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Microsoft.Extensions.Options;

namespace Dmart.Auth.OAuth;

// "Find or create" a dmart User from an OAuth provider's user info.
//
// Lookup chain:
//   1. By synthetic shortname `{provider}_{providerId}` — if the user has
//      logged in with this provider before, this is the fastest path and
//      also handles the case where they don't have an email.
//   2. Create new. Shortname is `{provider}_{providerId}`; is_email_verified
//      is set to true since the provider already verified it.
//
// Account takeover note: we deliberately do NOT fall back to "if an existing
// local account has the same email, attach the provider id to it." That path
// used to exist but was a pre-auth account takeover primitive — anyone able
// to register an OAuth provider account with a target's email address (which
// is the default, no reverse-verification) could take over the target's
// local dmart account on first OAuth login. Users who already have a local
// account get a second, separate account for OAuth logins; linking the two
// has to be a deliberate server-side ceremony, not a silent merge.
public sealed class OAuthUserResolver(
    UserRepository users,
    PluginManager plugins,
    IOptions<DmartSettings> settings,
    ILogger<OAuthUserResolver> log)
{
    // Python parity: api/user/router.py:61 defines USERS_SUBPATH="users" (no
    // leading slash) and passes that verbatim to every Event it dispatches in
    // this file. Plugin filter matching is case-sensitive and compares this
    // against EventFilter.subpaths exactly, so the bare "users" form is
    // load-bearing for plugin filters configured against the Python convention.
    private const string UsersSubpath = "users";

    public async Task<User?> ResolveAsync(OAuthUserInfo info, CancellationToken ct = default)
    {
        var shortname = BuildShortname(info.Provider, info.ProviderId);

        // Python parity (api/user/router.py:1337-1346): fire the before-hook
        // unconditionally at the top of find_or_create_social_user, before the
        // existence check. Plugins that care whether a create is actually about
        // to happen must check themselves. Exceptions are logged and swallowed
        // — unlike EntryService.CreateAsync, which translates a thrown before-
        // hook into a Result.Fail, Python's OAuth path doesn't let a plugin
        // block login. Don't surprise existing integrations.
        var preEvent = new Event
        {
            SpaceName = settings.Value.ManagementSpace,
            Subpath = UsersSubpath,
            Shortname = shortname,
            ActionType = ActionType.Create,
            ResourceType = ResourceType.User,
            UserShortname = shortname,
        };
        try { await plugins.BeforeActionAsync(preEvent, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "oauth: before-create plugin hook threw for {Shortname}; continuing", shortname);
        }

        // 1. Exact shortname match.
        var existing = await users.GetByShortnameAsync(shortname, ct);
        if (existing is not null)
            return await MaybeRefreshAsync(existing, info, ct);

        var byEmail = !string.IsNullOrEmpty(info.Email)
            ? await users.GetByEmailAsync(info.Email, ct)
            : null;
        if (byEmail is not null)
        {
            var updated = byEmail with
            {
                GoogleId = info.Provider == "google" ? info.ProviderId : byEmail.GoogleId,
                FacebookId = info.Provider == "facebook" ? info.ProviderId : byEmail.FacebookId,
                AppleId = info.Provider == "apple" ? info.ProviderId : byEmail.AppleId,
            };
            return await MaybeRefreshAsync(updated, info, ct);
        }
        // No dmart account matches this provider id or email. OAuth login no
        // longer auto-creates accounts — the caller turns null into a 401.
        return null;
    }

    // Keep display/picture/email fresh on repeat logins — the provider is
    // authoritative for these. Saves a DB round-trip for unchanged rows.
    private async Task<User> MaybeRefreshAsync(User user, OAuthUserInfo info, CancellationToken ct)
    {
        var dirty = false;
        var updated = user;
        if (!string.IsNullOrEmpty(info.Email) && info.Email != user.Email)
        {
            updated = updated with { Email = info.Email, IsEmailVerified = true };
            dirty = true;
        }
        if (!string.IsNullOrEmpty(info.PictureUrl) && info.PictureUrl != user.SocialAvatarUrl)
        {
            updated = updated with { SocialAvatarUrl = info.PictureUrl };
            dirty = true;
        }

        if (!dirty) return user;
        updated = updated with { UpdatedAt = TimeUtils.Now() };
        await users.UpsertAsync(updated, ct);
        return updated;
    }

    private static string BuildShortname(string provider, string providerId)
    {
        // dmart shortnames are alphanumeric + underscore. Sanitize the
        // provider id in case it carries characters that fail the regex.
        var sanitized = new string(providerId
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (sanitized.Length == 0) sanitized = "x";
        return $"{provider}_{sanitized}";
    }

    private static string? BuildDisplayName(string? first, string? last)
    {
        var name = $"{first?.Trim()} {last?.Trim()}".Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
