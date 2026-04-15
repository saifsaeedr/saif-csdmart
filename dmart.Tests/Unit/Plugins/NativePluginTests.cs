using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

public class NativePluginTests
{
    [Fact]
    public void NativeMarshal_RoundTrips_Utf8_String()
    {
        var original = "Hello, 世界! 🌍";
        var ptr = NativeMarshal.StringToUtf8(original);
        try
        {
            var result = NativeMarshal.Utf8ToString(ptr);
            result.ShouldBe(original);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void NativeMarshal_Utf8ToString_Returns_Empty_For_Null_Ptr()
    {
        NativeMarshal.Utf8ToString(IntPtr.Zero).ShouldBe("");
    }

    [Fact]
    public void NativePluginLoader_Does_Not_Throw()
    {
        // AddNativePlugins should never throw regardless of whether
        // ~/.dmart/plugins/ exists or has valid/invalid plugins
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Should.NotThrow(() => services.AddNativePlugins());
    }

    [Fact]
    public void NativePluginHandle_Load_Throws_On_Missing_File()
    {
        Should.Throw<DllNotFoundException>(() =>
            NativePluginHandle.Load("/nonexistent/path/plugin.so"));
    }

    [Fact]
    public void FindSharedLibrary_Returns_Null_For_Empty_Dir()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dmart_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            NativePluginLoader.FindSharedLibrary(tmpDir, "test").ShouldBeNull();
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FindSharedLibrary_Finds_Named_So()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dmart_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var soPath = Path.Combine(tmpDir, "myplugin.so");
        File.WriteAllBytes(soPath, new byte[] { 0 });
        try
        {
            NativePluginLoader.FindSharedLibrary(tmpDir, "myplugin").ShouldBe(soPath);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
