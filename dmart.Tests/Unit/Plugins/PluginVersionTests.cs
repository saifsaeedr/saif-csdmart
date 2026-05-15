using System.Reflection;
using Dmart.Plugins;
using Dmart.Plugins.BuiltIn;
using Dmart.Plugins.Native;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Pins the PluginManager.ResolveVersion fallback chain. The chain is the
// load-bearing primitive behind GET /info/plugins and the PLUGIN_LOADED log
// line; if it ever stops preferring the wrapper-supplied version over
// reflective assembly lookup, native .so + subprocess plugins would silently
// start reporting dmart's own version instead of their own — exactly the
// regression this test exists to catch.
public class PluginVersionTests
{
    [Fact]
    public void Wrapper_Supplied_Version_Wins_Over_Assembly_Attribute()
    {
        var stub = new VersionedStubHookPlugin("9.9.9");
        PluginManager.ResolveVersion(stub).ShouldBe("9.9.9");
    }

    [Fact]
    public void Wrapper_Empty_Version_Falls_Through_To_Assembly_Attribute()
    {
        // IPluginVersionSource present but empty → not "wrapper-supplied",
        // fall through to reflection. The stub lives in the dmart.Tests
        // assembly so we get back whatever AssemblyInformationalVersion or
        // AssemblyVersion the test assembly carries — non-empty either way.
        var stub = new VersionedStubHookPlugin("");
        var resolved = PluginManager.ResolveVersion(stub);
        resolved.ShouldNotBeNullOrEmpty();
        resolved.ShouldNotBe("0.0.0");  // tests assembly always has SOME version
    }

    [Fact]
    public void Builtin_Plugin_Resolves_To_Dmart_Assembly_Version()
    {
        // AuditPlugin lives in the dmart assembly, so its version IS dmart's
        // own AssemblyInformationalVersion. The build pipeline stamps that
        // attribute (see dmart.csproj <InformationalVersion>) so the test
        // assembly should see a non-empty value here in every CI run.
        var audit = new AuditPlugin(NullLogger<AuditPlugin>.Instance);
        var resolved = PluginManager.ResolveVersion(audit);

        resolved.ShouldNotBeNullOrEmpty();
        resolved.ShouldNotBe("0.0.0");

        // ResolveVersion strips the "branch=…" suffix that dmart's build
        // pipeline appends — verify the parser kept just the leading token
        // when the attribute carries the full informational form.
        var dmartAttr = typeof(Dmart.Api.Info.ManifestHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(dmartAttr) && dmartAttr.Contains("branch="))
            resolved.ShouldNotContain("branch=");
    }

    [Fact]
    public void IPluginVersionSource_Discriminates_Wrappers_Correctly()
    {
        // Sanity: the BuiltIn AuditPlugin must NOT implement IPluginVersionSource
        // (its version source is the assembly), but the stub does. Encodes the
        // contract the loader relies on. Cast through `object` so the compiler
        // doesn't statically narrow the `is` check (IPluginVersionSource is
        // internal and AuditPlugin doesn't implement it — without the cast,
        // CS0184 fires because the result is provably false at compile time,
        // which is exactly the property we want to assert at RUNTIME).
        ((object)new AuditPlugin(NullLogger<AuditPlugin>.Instance) is IPluginVersionSource)
            .ShouldBeFalse();
        (new VersionedStubHookPlugin("1.0.0") as IPluginVersionSource).ShouldNotBeNull();
    }

    [Fact]
    public void PluginInfo_Carries_Shortname_Version_Type()
    {
        // Trivial round-trip on the public record so a future field rename
        // doesn't silently break the /info/plugins endpoint shape.
        var info = new PluginInfo("audit", "1.2.3", "hook");
        info.Shortname.ShouldBe("audit");
        info.Version.ShouldBe("1.2.3");
        info.Type.ShouldBe("hook");
    }

    // --- Stubs ---------------------------------------------------------------

    // Implements IPluginVersionSource the way SubprocessHookPlugin does:
    // wrapper-supplied version that PluginManager should prefer over
    // reflective assembly lookup.
    private sealed class VersionedStubHookPlugin : IHookPlugin, IPluginVersionSource
    {
        public VersionedStubHookPlugin(string version) => PluginVersion = version;
        public string Shortname => "stub";
        public string PluginVersion { get; }
        public Task HookAsync(Dmart.Models.Core.Event e, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // Stub logger so the AuditPlugin ctor doesn't need a real DI container.
    private sealed class NullLogger<T> : ILogger<T>
    {
        public static readonly NullLogger<T> Instance = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
