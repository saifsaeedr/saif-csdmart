using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SampleHook;

public static class Plugin
{
    [UnmanagedCallersOnly(EntryPoint = "get_info")]
    public static IntPtr GetInfo()
        => AllocUtf8("""{"shortname":"sample_hook","type":"hook"}""");

    // Optional: returns the plugin's version string. dmart resolves this via
    // dlsym(handle, "dmart_plugin_version") at load time and surfaces it on
    // GET /info/plugins and the NATIVE_PLUGIN_REGISTERED log line.
    //
    // The pointer is process-lifetime — dmart does NOT call free_string on it.
    // Marshal.StringToHGlobalAnsi keeps the bytes alive for the lifetime of
    // the .so. The literal mirrors <Version> in sample_hook.csproj; in a real
    // pipeline you'd source-gen this file from your build's version constant
    // so the two never drift.
    [UnmanagedCallersOnly(EntryPoint = "dmart_plugin_version")]
    public static IntPtr GetVersion() => StaticVersionPtr;

    private static readonly IntPtr StaticVersionPtr = Marshal.StringToHGlobalAnsi("1.0.0");

    [UnmanagedCallersOnly(EntryPoint = "hook")]
    public static IntPtr Hook(IntPtr eventJsonPtr)
    {
        try
        {
            var json = Marshal.PtrToStringUTF8(eventJsonPtr) ?? "";
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action_type", out var a) ? a.GetString() : "?";
            var space = root.TryGetProperty("space_name", out var s) ? s.GetString() : "?";
            var sn = root.TryGetProperty("shortname", out var n) ? n.GetString() : "?";
            Console.Error.WriteLine($"[sample_hook] {action} {space}/{sn}");
            return AllocUtf8("""{"status":"ok"}""");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sample_hook] ERROR: {ex.Message}");
            return AllocUtf8($$"""{"status":"error","message":"{{ex.Message}}"}""");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "free_string")]
    public static void FreeString(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
    }

    private static IntPtr AllocUtf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }
}
