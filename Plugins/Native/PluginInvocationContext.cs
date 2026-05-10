namespace Dmart.Plugins.Native;

// Ambient actor for the duration of a single native-plugin invocation.
//
// Set by the native-plugin dispatchers (NativeHookPlugin.HookAsync,
// NativeApiPlugin.HandleNative) immediately before the unmanaged call,
// then restored in the matching finally. Read by host callbacks the
// plugin makes (e.g. NativePluginCallbacks.QueryCb) to decide whose
// permissions to apply when the plugin doesn't override via "as_actor".
//
// Storage discipline — [ThreadStatic] is load-bearing.
//   * The native call itself is synchronous from the C side
//     (CallHook / CallHandleRequest are sync wrappers around dlsym'd
//     function pointers). Any host callback the plugin invokes runs on
//     the SAME thread that entered the native call. So the value set
//     just before the unmanaged call is exactly what the callback reads.
//   * Switching to AsyncLocal would cost an allocation per dispatch
//     for no benefit — there is no `await` between set and read.
//   * A plain `static` would race across concurrent plugin invocations
//     on different threads — never use that here.
//
// Nesting: dispatchers must save the previous value and restore it in a
// `finally`, in case a plugin (or a hook the plugin itself triggers via
// a Save/Update callback) re-enters the dispatcher synchronously.
internal static class PluginInvocationContext
{
    [ThreadStatic]
    private static string? _currentActor;

    public static string? CurrentActor
    {
        get => _currentActor;
        set => _currentActor = value;
    }

    // Shortname of the plugin currently executing on this thread. Read by
    // NativePluginCallbacks.LogCb to prefix the log category as
    // `plugin.<shortname>[.<sub>]` — prevents a plugin from impersonating
    // unrelated categories.
    [ThreadStatic]
    private static string? _currentShortname;

    public static string? CurrentShortname
    {
        get => _currentShortname;
        set => _currentShortname = value;
    }
}
