using Dmart.Models.Core;

namespace Dmart.Plugins.BuiltIn;

// Stub port of dmart/backend/plugins/system_notification_sender/plugin.py.
// Python version runs after most CRUD actions, looks up matching
// SystemNotificationRequest entries in management/notifications/system, fans
// out to owner + group members, writes a per-user Content notification entry,
// and pushes via NotificationManager.
//
// The query + push gateway sides aren't ported (no Firebase bridge). Registered
// so config.json references don't warn. Replace HookAsync when the push pipeline
// is available.
public sealed class SystemNotificationSenderPlugin(ILogger<SystemNotificationSenderPlugin> log) : IHookPlugin
{
    public string Shortname => "system_notification_sender";

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        log.LogDebug("system_notification_sender: stub (no-op) for {Space}/{Subpath}/{Shortname}",
            e.SpaceName, e.Subpath, e.Shortname ?? "-");
        return Task.CompletedTask;
    }
}
