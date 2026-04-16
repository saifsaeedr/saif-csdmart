using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// IHookPlugin adapter that delegates to a subprocess via stdin/stdout JSON lines.
// If the subprocess crashes, it's respawned automatically on the next event.
internal sealed class SubprocessHookPlugin(SubprocessPluginHost host) : IHookPlugin
{
    public string Shortname => host.Shortname;

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        var eventJson = JsonSerializer.Serialize(e, DmartJsonContext.Default.Event);
        var request = $"{{\"type\":\"hook\",\"event\":{eventJson}}}";
        var response = host.SendAndReceive(request);

        if (!string.IsNullOrEmpty(response))
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("status", out var status)
                && status.GetString() == "error")
            {
                var message = doc.RootElement.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "Plugin error"
                    : "Plugin error";
                throw new InvalidOperationException(message);
            }
        }

        return Task.CompletedTask;
    }
}
