using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// Scans ~/.dmart/plugins/<name>/ directories for external plugins.
//
// Two loading modes (tried in order):
//   1. Executable (subprocess) — crash-safe, auto-respawn. The plugin is a
//      standalone binary that reads JSON lines from stdin and writes to stdout.
//   2. Shared library (.so) — in-process, fastest, but a segfault crashes dmart.
//
// Both modes create IHookPlugin/IApiPlugin adapters so PluginManager dispatches
// to them identically to built-in plugins.
public static class NativePluginLoader
{
    // Every SubprocessPluginHost we spawn. Walked on ApplicationStopping so
    // each subprocess gets a clean stdin-close (EOF) shutdown before the
    // dotnet process exits. Without this, subprocesses only find out dmart
    // is gone when their next stdin write raises a broken-pipe.
    private static readonly List<SubprocessPluginHost> _hosts = new();

    public static void AddNativePlugins(this IServiceCollection services)
    {
        var customRoot = FindPluginsRoot();
        if (customRoot is null) return;

        foreach (var dir in Directory.EnumerateDirectories(customRoot))
        {
            var dirName = Path.GetFileName(dir);
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;

            // Prefer executable (subprocess, crash-safe) over .so (in-process)
            var execPath = FindExecutable(dir, dirName);
            if (execPath is not null)
            {
                LoadSubprocessPlugin(services, execPath, dirName);
                continue;
            }

            var soPath = FindSharedLibrary(dir, dirName);
            if (soPath is not null)
            {
                LoadNativePlugin(services, soPath, dirName);
                continue;
            }
        }
    }

    private static void LoadSubprocessPlugin(IServiceCollection services, string execPath, string dirName)
    {
        try
        {
            var host = new SubprocessPluginHost(execPath, dirName);
            _hosts.Add(host);

            // Ask the plugin for its info
            var infoJson = host.SendAndReceive("{\"type\":\"info\"}");
            using var infoDoc = JsonDocument.Parse(infoJson);
            var root = infoDoc.RootElement;

            var shortname = root.TryGetProperty("shortname", out var sn)
                ? sn.GetString() ?? dirName : dirName;
            var typeStr = root.TryGetProperty("type", out var tp)
                ? tp.GetString() ?? "hook" : "hook";

            if (typeStr == "hook")
            {
                services.AddSingleton<IHookPlugin>(new SubprocessHookPlugin(host));
                Console.WriteLine($"SUBPROCESS_PLUGIN_REGISTERED: {shortname} (hook) from {execPath}");
            }
            else if (typeStr == "api")
            {
                var routes = ParseRoutes(root);
                services.AddSingleton<IApiPlugin>(new SubprocessApiPlugin(host, routes));
                Console.WriteLine($"SUBPROCESS_PLUGIN_REGISTERED: {shortname} (api, {routes.Count} routes) from {execPath}");
            }
            else
            {
                Console.WriteLine($"SUBPROCESS_PLUGIN_ERROR: {shortname} unknown type '{typeStr}'");
                host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SUBPROCESS_PLUGIN_LOAD_FAILED: {dirName}: {ex.Message}");
        }
    }

    private static void LoadNativePlugin(IServiceCollection services, string soPath, string dirName)
    {
        try
        {
            var handle = NativePluginHandle.Load(soPath);
            var infoJson = handle.CallGetInfo();
            using var infoDoc = JsonDocument.Parse(infoJson);
            var root = infoDoc.RootElement;

            var shortname = root.TryGetProperty("shortname", out var sn)
                ? sn.GetString() ?? dirName : dirName;
            var typeStr = root.TryGetProperty("type", out var tp)
                ? tp.GetString() ?? "hook" : "hook";

            if (typeStr == "hook")
            {
                if (handle.Hook is null)
                {
                    Console.WriteLine($"NATIVE_PLUGIN_ERROR: {shortname} type=hook but no hook() export");
                    handle.Dispose();
                    return;
                }
                services.AddSingleton<IHookPlugin>(new NativeHookPlugin(handle, shortname));
                Console.WriteLine($"NATIVE_PLUGIN_REGISTERED: {shortname} (hook, in-process) from {soPath}");
            }
            else if (typeStr == "api")
            {
                if (handle.HandleRequest is null)
                {
                    Console.WriteLine($"NATIVE_PLUGIN_ERROR: {shortname} type=api but no handle_request() export");
                    handle.Dispose();
                    return;
                }
                var routes = ParseRoutes(root);
                services.AddSingleton<IApiPlugin>(new NativeApiPlugin(handle, shortname, routes));
                Console.WriteLine($"NATIVE_PLUGIN_REGISTERED: {shortname} (api, in-process) from {soPath}");
            }
            else
            {
                Console.WriteLine($"NATIVE_PLUGIN_ERROR: {shortname} unknown type '{typeStr}'");
                handle.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NATIVE_PLUGIN_LOAD_FAILED: {dirName}: {ex.Message}");
        }
    }

    // Also loads config.json files from the native plugins directory so
    // PluginManager can register them in its dispatch tables.
    public static List<PluginWrapper> LoadNativeConfigs()
    {
        var configs = new List<PluginWrapper>();
        var customRoot = FindPluginsRoot();
        if (customRoot is null) return configs;

        foreach (var dir in Directory.EnumerateDirectories(customRoot))
        {
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;

            try
            {
                var json = File.ReadAllText(configPath);
                var wrapper = JsonSerializer.Deserialize(json, DmartJsonContext.Default.PluginWrapper);
                if (wrapper is not null)
                {
                    wrapper.Shortname = Path.GetFileName(dir);
                    configs.Add(wrapper);
                }
            }
            catch { /* skip malformed configs */ }
        }

        return configs;
    }

    private static string? FindPluginsRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var homePath = Path.Combine(home, ".dmart", "plugins");
            if (Directory.Exists(homePath)) return homePath;
        }
        return null;
    }

    // Find an executable file (not .so) — for subprocess mode
    internal static string? FindExecutable(string dir, string dirName)
    {
        // Try <dirname> (exact name, no extension)
        var exact = Path.Combine(dir, dirName);
        if (File.Exists(exact) && IsExecutable(exact)) return exact;

        // Try any file without a common library extension that is executable
        foreach (var file in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".json" or ".so" or ".dylib" or ".dll" or ".dbg" or ".pdb" or ".md" or ".txt")
                continue;
            if (IsExecutable(file)) return file;
        }

        return null;
    }

    internal static string? FindSharedLibrary(string dir, string dirName)
    {
        var simple = Path.Combine(dir, $"{dirName}.so");
        if (File.Exists(simple)) return simple;

        var lib = Path.Combine(dir, $"lib{dirName}.so");
        if (File.Exists(lib)) return lib;

        foreach (var ext in new[] { "*.so", "*.dylib", "*.dll" })
        {
            var files = Directory.GetFiles(dir, ext);
            if (files.Length > 0) return files[0];
        }

        return null;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            // Check Unix executable bit
            var info = new FileInfo(path);
            return info.Exists && (info.UnixFileMode & UnixFileMode.UserExecute) != 0;
        }
        catch { return false; }
    }

    // Invoke from Program.cs once the WebApplication has been built, so we
    // can register a graceful-shutdown callback on IHostApplicationLifetime.
    // For subprocess plugins, this sends an EOF on their stdin so they exit
    // cleanly on the next read rather than learning about shutdown via a
    // broken-pipe (or tripping a KeyboardInterrupt on terminal Ctrl+C).
    public static void WireSubprocessShutdown(IHostApplicationLifetime lifetime)
    {
        lifetime.ApplicationStopping.Register(() =>
        {
            foreach (var h in _hosts)
            {
                try { h.Shutdown(); } catch { /* best-effort */ }
            }
        });
    }

    private static List<NativeApiPlugin.NativeRoute> ParseRoutes(JsonElement root)
    {
        var routes = new List<NativeApiPlugin.NativeRoute>();
        if (root.TryGetProperty("routes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                var method = r.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
                var path = r.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
                routes.Add(new NativeApiPlugin.NativeRoute(method, path));
            }
        }
        return routes;
    }
}
