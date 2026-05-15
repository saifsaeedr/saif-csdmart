using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;

namespace Dmart.Plugins.BuiltIn;

// Local C# extension — there's no audit plugin in upstream dmart. Logs every
// dispatched event at Information level. Useful for debugging the plugin
// pipeline. The shipped config.json declares
// `subpaths: { "__all_spaces__": ["__all_subpaths__"] }` so it fires on every
// create/update/delete/etc. across every space.
public sealed class AuditPlugin(ILogger<AuditPlugin> log) : IHookPlugin
{
    public string Shortname => "audit";

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        // Bulk imports (CSV today, potentially zip later) tag each per-row Event
        // with IsBulkImport=true. Logging every row would flood the audit log
        // with thousands of lines for one operator action — the HTTP-level
        // request log already records the bulk call once, which is enough.
        if (e.IsBulkImport) return Task.CompletedTask;

        log.LogInformation("{Action} {Space}/{Subpath}/{Shortname} by {User}",
            JsonbHelpers.EnumMember(e.ActionType),
            e.SpaceName, e.Subpath, e.Shortname ?? "-", e.UserShortname);
        return Task.CompletedTask;
    }
}
