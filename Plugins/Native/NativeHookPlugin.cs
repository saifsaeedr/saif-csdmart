using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// Adapts a native .so hook plugin behind IHookPlugin so PluginManager
// dispatches to it identically to built-in plugins.
//
// `pluginVersion` is the version string the loader read from the .so via
// dlsym(dmart_plugin_version) (see NativePluginHandle.CallGetVersion).
// Surfaced via IPluginVersionSource so PluginManager.ResolveVersion can
// prefer it over reflective assembly lookup (which would incorrectly return
// dmart's own version for this wrapper).
internal sealed class NativeHookPlugin(NativePluginHandle handle, string shortname,
    string pluginVersion = "0.0.0") : IHookPlugin, IPluginVersionSource
{
    public string Shortname => shortname;
    public string PluginVersion { get; } = pluginVersion;

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(e, DmartJsonContext.Default.Event);

        // Expose the calling actor to host callbacks (e.g. QueryCb) so plugin
        // queries default to the user's permissions, and the plugin's
        // shortname so LogCb can prefix log categories. Restore previous
        // values rather than nulling to keep nested invocations safe.
        //
        // Both context fields are [ThreadStatic]. That's safe here because
        // handle.CallHook is a synchronous P/Invoke — no await between set
        // and restore — so the native code (and any LogCb it triggers) runs
        // on the same thread that set the values. If this ever moves to an
        // async-bridge model (e.g. Task.Run for native dispatch), switch to
        // AsyncLocal<T> or the values will leak across awaits.
        var previousActor = PluginInvocationContext.CurrentActor;
        var previousShortname = PluginInvocationContext.CurrentShortname;
        PluginInvocationContext.CurrentActor = e.UserShortname;
        PluginInvocationContext.CurrentShortname = shortname;
        string? result;
        try
        {
            result = handle.CallHook(json);
        }
        finally
        {
            PluginInvocationContext.CurrentActor = previousActor;
            PluginInvocationContext.CurrentShortname = previousShortname;
        }

        if (!string.IsNullOrEmpty(result))
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("status", out var status)
                && status.GetString() == "error")
            {
                var message = doc.RootElement.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "Native plugin error"
                    : "Native plugin error";
                throw new InvalidOperationException(message);
            }
        }

        return Task.CompletedTask;
    }
}
