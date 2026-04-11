using Dmart.Models.Core;

namespace Dmart.Plugins.BuiltIn;

// Stub port of dmart/backend/plugins/admin_notification_sender/plugin.py.
// The Python version reads a just-created "admin_notification_request" entry,
// resolves the target msisdns via a query, and pushes via
// NotificationManager (Firebase/SMS bridge).
//
// The SMS/push gateway is explicitly out of scope for the port (see the OTP
// delivery stub). Registering the shortname prevents PLUGIN_UNKNOWN warnings
// when a config.json entry references it; replace HookAsync once a gateway
// integration exists.
public sealed class AdminNotificationSenderPlugin(ILogger<AdminNotificationSenderPlugin> log) : IHookPlugin
{
    public string Shortname => "admin_notification_sender";

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        log.LogDebug("admin_notification_sender: stub (no-op) for {Space}/{Subpath}/{Shortname}",
            e.SpaceName, e.Subpath, e.Shortname ?? "-");
        return Task.CompletedTask;
    }
}
