using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;

namespace Dmart.Plugins.BuiltIn;

// Local C# extension — there's no audit plugin in upstream dmart. Logs every
// dispatched event at Information level. Useful for debugging the plugin
// pipeline and is intentionally ungated by filters: any space that adds
// "audit" to its active_plugins list will see every create/update/delete.
public sealed class AuditPlugin(ILogger<AuditPlugin> log) : IHookPlugin
{
    public string Shortname => "audit";

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        log.LogInformation("audit: {Action} {Space}/{Subpath}/{Shortname} by {User}",
            JsonbHelpers.EnumMember(e.ActionType),
            e.SpaceName, e.Subpath, e.Shortname ?? "-", e.UserShortname);
        return Task.CompletedTask;
    }
}
