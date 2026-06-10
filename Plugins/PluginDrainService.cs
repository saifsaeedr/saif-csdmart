using Microsoft.Extensions.Hosting;

namespace Dmart.Plugins;

// On graceful shutdown, wait (bounded) for fire-and-forget concurrent plugin
// after-hooks to finish before the process exits, so they aren't cut off
// mid-write. Uses IHostedService.StopAsync (async-native) rather than an
// ApplicationStopping callback (sync, would need a blocking wait). Runs inside
// the host's default shutdown budget.
public sealed class PluginDrainService(PluginManager plugins, ILogger<PluginDrainService> log)
    : IHostedService
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var drained = await plugins.DrainAsync(DrainTimeout, cancellationToken);
        if (!drained)
            log.LogWarning(
                "plugin drain timed out after {Seconds}s — some concurrent after-hooks may not have finished",
                DrainTimeout.TotalSeconds);
    }
}
