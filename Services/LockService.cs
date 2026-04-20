using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

public sealed class LockService(LockRepository locks, IOptions<DmartSettings> settings)
{
    public async Task<Response> LockAsync(Locator l, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actor))
            return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);
        var period = settings.Value.LockPeriod;
        var ok = await locks.TryLockAsync(l.SpaceName, l.Subpath, l.Shortname, actor, period, ct);
        if (ok)
        {
            // Include lock_period so clients know how long they can hold the
            // lock before refreshing. Matches Python's /managed/lock response.
            return Response.Ok(attributes: new()
            {
                ["locked_by"] = actor,
                ["lock_period"] = period,
            });
        }
        var holder = await locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, period, ct);
        return Response.Fail(InternalErrorCode.LOCKED_ENTRY, $"already locked by {holder}", ErrorTypes.Db);
    }

    public async Task<Response> UnlockAsync(Locator l, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actor))
            return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);
        var ok = await locks.UnlockAsync(l.SpaceName, l.Subpath, l.Shortname, actor, ct);
        return ok
            ? Response.Ok()
            : Response.Fail(InternalErrorCode.NOT_ALLOWED, "you don't hold this lock", ErrorTypes.Auth);
    }

    public Task<string?> GetLockerAsync(Locator l, CancellationToken ct = default)
        => locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, settings.Value.LockPeriod, ct);
}
