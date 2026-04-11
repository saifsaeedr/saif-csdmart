using Dmart.Models.Core;

namespace Dmart.Plugins.BuiltIn;

// Stub port of dmart/backend/plugins/ldap_manager/plugin.py. The Python version
// mirrors user create/update/delete/move into an LDAP directory via ldap3.
// Porting requires an LDAP client library (Novell.Directory.Ldap.NETStandard
// or similar) plus settings plumbing (ldap_url, ldap_admin_dn, ldap_pass,
// ldap_root_dn), none of which exist yet.
//
// Registered so a config.json entry referencing this plugin doesn't warn.
// The upstream config ships with is_active=false so it won't activate by default.
public sealed class LdapManagerPlugin(ILogger<LdapManagerPlugin> log) : IHookPlugin
{
    public string Shortname => "ldap_manager";

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        log.LogDebug("ldap_manager: stub (no-op) for {Space}/{Subpath}/{Shortname}",
            e.SpaceName, e.Subpath, e.Shortname ?? "-");
        return Task.CompletedTask;
    }
}
