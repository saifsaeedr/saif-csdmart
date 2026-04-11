using Dmart.Models.Core;

namespace Dmart.Plugins;

// Mirrors dmart/backend/models/core.py::PluginBase. A hook plugin gets invoked
// on before/after action events whose shape matches its config.json filters.
//
// The Shortname must exactly match the shortname in the plugin's config.json;
// PluginManager uses it to associate the config with the concrete C# instance.
//
// If HookAsync throws from a BEFORE hook, PluginManager surfaces the exception
// and the originating action is aborted. After-hook exceptions are logged but
// do not fail the action (matching Python semantics).
public interface IHookPlugin
{
    string Shortname { get; }
    Task HookAsync(Event e, CancellationToken ct = default);
}
