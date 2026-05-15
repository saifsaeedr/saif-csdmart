using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SampleApi;

// Sample native API plugin. Mounts endpoints under /sample_api/.
//
// Build:  dotnet publish -c Release -r linux-x64
// Deploy: cp bin/Release/net10.0/linux-x64/publish/sample_api.so ~/.dmart/plugins/sample_api/
public static class Plugin
{
    [UnmanagedCallersOnly(EntryPoint = "get_info")]
    public static IntPtr GetInfo()
        => AllocUtf8("""
            {
              "shortname": "sample_api",
              "type": "api",
              "routes": [
                {"method": "GET", "path": "/"},
                {"method": "GET", "path": "/greet/{name}"}
              ]
            }
            """);

    // Optional: surfaces the plugin's version on GET /info/plugins. See
    // sample_hook/Plugin.cs for the rationale + ownership rules.
    [UnmanagedCallersOnly(EntryPoint = "dmart_plugin_version")]
    public static IntPtr GetVersion() => StaticVersionPtr;

    private static readonly IntPtr StaticVersionPtr = Marshal.StringToHGlobalAnsi("1.0.0");

    // Receives JSON: {"method":"GET","path":"/sample_api/greet/alice","query":{},"headers":{},"body":null,"user":"dmart"}
    // Returns JSON: dmart Response shape
    [UnmanagedCallersOnly(EntryPoint = "handle_request")]
    public static IntPtr HandleRequest(IntPtr requestJsonPtr)
    {
        try
        {
            var json = Marshal.PtrToStringUTF8(requestJsonPtr) ?? "";
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
            var user = root.TryGetProperty("user", out var u) ? u.GetString() ?? "anonymous" : "anonymous";

            if (path.Contains("/greet/"))
            {
                var name = path.Split("/greet/", 2)[1].Trim('/');
                if (string.IsNullOrEmpty(name)) name = "world";
                return AllocUtf8("{\"status\":\"success\",\"attributes\":{\"greeting\":\"Hello, " + name + "!\",\"plugin\":\"sample_api\",\"user\":\"" + user + "\"}}");
            }

            return AllocUtf8("{\"status\":\"success\",\"attributes\":{\"plugin\":\"sample_api\",\"description\":\"A sample native API plugin\",\"user\":\"" + user + "\"}}");
        }
        catch (Exception ex)
        {
            return AllocUtf8("{\"status\":\"failed\",\"error\":{\"type\":\"plugin_error\",\"code\":500,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}}");
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
