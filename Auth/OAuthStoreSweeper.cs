namespace Dmart.Auth;

// Periodic background task that removes expired authorization codes from
// OAuthCodeStore and stale clients from OAuthClientStore. Without this,
// issued-but-never-redeemed codes accumulate indefinitely in the
// ConcurrentDictionary (Consume only removes on redemption).
//
// Runs every 5 minutes. Lightweight: iterates snapshot keys and removes
// entries whose ExpiresAt / CreatedAt have passed their TTL.
public sealed class OAuthStoreSweeper(OAuthCodeStore codeStore, OAuthClientStore clientStore) : IHostedService, IDisposable
{
    private Timer? _timer;

    // Codes expire after 60 s but sweep generously at 5 min intervals.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Clients registered more than 24 h ago without any authorize flow are
    // almost certainly abandoned MCP registrations. Real clients re-register
    // on startup, so this is safe.
    private static readonly TimeSpan ClientMaxAge = TimeSpan.FromHours(24);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Sweep(), null, Interval, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void Sweep()
    {
        codeStore.RemoveExpired();
        clientStore.RemoveOlderThan(ClientMaxAge);
    }

    public void Dispose() => _timer?.Dispose();
}
