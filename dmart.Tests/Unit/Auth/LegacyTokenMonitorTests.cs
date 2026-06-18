using Dmart.Auth;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

public class LegacyTokenMonitorTests
{
    [Fact]
    public void Record_Increments_Count()
    {
        var monitor = new LegacyTokenMonitor();
        monitor.Count.ShouldBe(0);
        monitor.Record("alice", "bearer");
        monitor.Record(null, "ws");
        monitor.Count.ShouldBe(2);
    }
}
