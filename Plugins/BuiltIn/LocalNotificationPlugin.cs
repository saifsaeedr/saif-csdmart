using Dmart.Models.Core;

namespace Dmart.Plugins.BuiltIn;

// Stub port of dmart/backend/plugins/local_notification/plugin.py. The Python
// version materializes a Content notification in personal/people/.../notifications,
// stores a JSON payload, and optionally broadcasts via websocket.
//
// We register the shortname so config.json entries don't log PLUGIN_UNKNOWN,
// but the real implementation requires the notification-persistence pipeline
// we haven't ported yet (attachment-first notifications + schema "notification").
// Activating this plugin in a space is a no-op today; once the notification
// pipeline lands, replace HookAsync with the real body.
public sealed class LocalNotificationPlugin(ILogger<LocalNotificationPlugin> log) : IHookPlugin
{
    public string Shortname => "local_notification";

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        log.LogDebug("local_notification: stub (no-op) for {Space}/{Subpath}/{Shortname}",
            e.SpaceName, e.Subpath, e.Shortname ?? "-");
        return Task.CompletedTask;
    }
}
