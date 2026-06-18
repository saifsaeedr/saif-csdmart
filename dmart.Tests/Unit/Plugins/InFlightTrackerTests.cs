using System.Diagnostics;
using Dmart.Plugins;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// The shutdown drain for fire-and-forget concurrent plugin hooks. On graceful
// shutdown the host must wait (bounded) for in-flight after-hooks to finish
// rather than tearing them down mid-write. InFlightTracker is the mechanism
// PluginManager delegates to.
public sealed class InFlightTrackerTests
{
    [Fact]
    public async Task DrainAsync_Returns_Immediately_When_Nothing_In_Flight()
    {
        var tracker = new InFlightTracker();
        var sw = Stopwatch.StartNew();
        var drained = await tracker.DrainAsync(TimeSpan.FromSeconds(10));
        sw.Stop();

        drained.ShouldBeTrue();
        // Must NOT wait out the timeout — a no-op drain runs on every test's
        // host teardown, so this has to be effectively instant.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DrainAsync_Waits_For_In_Flight_Task()
    {
        var tracker = new InFlightTracker();
        var gate = new TaskCompletionSource();
        tracker.Track(Task.Run(async () => await gate.Task));

        var drain = tracker.DrainAsync(TimeSpan.FromSeconds(10));
        drain.IsCompleted.ShouldBeFalse("drain must not finish while a hook is still running");

        gate.SetResult();
        (await drain).ShouldBeTrue("drain should report success once the hook completes");
    }

    [Fact]
    public async Task DrainAsync_Returns_Within_Budget_When_Task_Hangs()
    {
        var tracker = new InFlightTracker();
        var never = new TaskCompletionSource();
        tracker.Track(Task.Run(async () => await never.Task));

        var sw = Stopwatch.StartNew();
        var drained = await tracker.DrainAsync(TimeSpan.FromMilliseconds(200));
        sw.Stop();

        drained.ShouldBeFalse("a hung hook must report a timed-out drain");
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "drain must honor its timeout budget");

        never.SetResult(); // let the background task unwind
    }
}
