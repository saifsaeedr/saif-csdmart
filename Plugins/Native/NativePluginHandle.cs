using System.Runtime.InteropServices;

namespace Dmart.Plugins.Native;

// Loads a native shared library (.so/.dylib/.dll) and resolves its exported
// C-ABI plugin functions. Implements IDisposable to unload the library.
//
// Exports:
//   IntPtr get_info()                          — returns JSON metadata
//   IntPtr hook(IntPtr event_json)             — hook plugin entry point
//   IntPtr handle_request(IntPtr request_json) — API plugin entry point
//   void   free_string(IntPtr ptr)             — frees returned strings
internal sealed class NativePluginHandle : IDisposable
{
    private IntPtr _lib;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetInfoFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr HookFn(IntPtr eventJson);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr HandleRequestFn(IntPtr requestJson);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FreeStringFn(IntPtr ptr);
    // Optional. Plugins that need to call back into dmart export `init` and
    // receive the pointer to a DmartCallbacks struct (see NativePluginCallbacks).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void InitFn(IntPtr callbacksPtr);

    public GetInfoFn GetInfo { get; }
    public HookFn? Hook { get; }
    public HandleRequestFn? HandleRequest { get; }
    public FreeStringFn FreeString { get; }
    public InitFn? Init { get; }
    public string SoPath { get; }

    private NativePluginHandle(IntPtr lib, string soPath,
        GetInfoFn getInfo, FreeStringFn freeString,
        HookFn? hook, HandleRequestFn? handleRequest, InitFn? init)
    {
        _lib = lib;
        SoPath = soPath;
        GetInfo = getInfo;
        FreeString = freeString;
        Hook = hook;
        HandleRequest = handleRequest;
        Init = init;
    }

    public static NativePluginHandle Load(string soPath)
    {
        var lib = NativeLibrary.Load(soPath);

        var getInfo = Marshal.GetDelegateForFunctionPointer<GetInfoFn>(
            NativeLibrary.GetExport(lib, "get_info"));
        var freeString = Marshal.GetDelegateForFunctionPointer<FreeStringFn>(
            NativeLibrary.GetExport(lib, "free_string"));

        HookFn? hook = null;
        if (NativeLibrary.TryGetExport(lib, "hook", out var hookPtr))
            hook = Marshal.GetDelegateForFunctionPointer<HookFn>(hookPtr);

        HandleRequestFn? handleRequest = null;
        if (NativeLibrary.TryGetExport(lib, "handle_request", out var hrPtr))
            handleRequest = Marshal.GetDelegateForFunctionPointer<HandleRequestFn>(hrPtr);

        InitFn? init = null;
        if (NativeLibrary.TryGetExport(lib, "init", out var initPtr))
            init = Marshal.GetDelegateForFunctionPointer<InitFn>(initPtr);

        return new NativePluginHandle(lib, soPath, getInfo, freeString, hook, handleRequest, init);
    }

    public string CallGetInfo()
    {
        var ptr = GetInfo();
        try { return NativeMarshal.Utf8ToString(ptr); }
        finally { FreeString(ptr); }
    }

    public string CallHook(string eventJson)
    {
        var input = NativeMarshal.StringToUtf8(eventJson);
        try
        {
            var resultPtr = Hook!(input);
            try { return NativeMarshal.Utf8ToString(resultPtr); }
            finally { FreeString(resultPtr); }
        }
        finally { Marshal.FreeHGlobal(input); }
    }

    public string CallHandleRequest(string requestJson)
    {
        var input = NativeMarshal.StringToUtf8(requestJson);
        try
        {
            var resultPtr = HandleRequest!(input);
            try { return NativeMarshal.Utf8ToString(resultPtr); }
            finally { FreeString(resultPtr); }
        }
        finally { Marshal.FreeHGlobal(input); }
    }

    public void Dispose()
    {
        if (_lib != IntPtr.Zero)
        {
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
        }
    }
}
