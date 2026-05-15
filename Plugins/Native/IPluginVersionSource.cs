namespace Dmart.Plugins.Native;

// Marker interface implemented by plugin wrapper classes whose runtime type
// lives in the dmart assembly but whose effective version belongs to an
// external artifact (a native .so, a subprocess executable). Lets
// PluginManager.ResolveVersion prefer the wrapper-supplied version over the
// reflective assembly attribute, which would incorrectly report dmart's own
// version for these wrappers.
//
// In-process .NET plugins (the BuiltIn classes) do NOT implement this — their
// runtime type's assembly IS the source of truth, so reflection on
// AssemblyInformationalVersionAttribute resolves correctly.
internal interface IPluginVersionSource
{
    string PluginVersion { get; }
}
