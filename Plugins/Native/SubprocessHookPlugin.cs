using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// IHookPlugin adapter that delegates to a subprocess via stdin/stdout JSON lines.
// If the subprocess crashes, it's respawned automatically on the next event.
//
// `pluginVersion` is the version string the subprocess reported in its
// {"type":"info"} response. Surfaced via IPluginVersionSource so
// PluginManager.ResolveVersion can prefer it over reflective assembly lookup
// (which would incorrectly return dmart's own version for this wrapper).
internal sealed class SubprocessHookPlugin(SubprocessPluginHost host, string pluginVersion = "0.0.0")
    : IHookPlugin, IPluginVersionSource
{
    public string Shortname => host.Shortname;
    public string PluginVersion { get; } = pluginVersion;

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
