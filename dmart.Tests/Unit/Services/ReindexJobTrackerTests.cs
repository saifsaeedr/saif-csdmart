using System.Linq;
using System.Threading.Tasks;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the single-flight gate, atomic snapshot publication, and last-run
// retention of ReindexJobTracker — the concurrency-sensitive core of the
// background reindex feature.
public class ReindexJobTrackerTests
{
    [Fact]
    public void TryStart_Is_SingleFlight()
    {
        var t = new ReindexJobTracker();
        t.TryStart("space-a").ShouldNotBeNull();
        t.TryStart("space-b").ShouldBeNull("a second concurrent start must be refused");
        t.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public void Finish_Releases_The_Slot()
    {
        var t = new ReindexJobTracker();
        t.TryStart("space-a").ShouldNotBeNull();
        t.Finish("completed");
        t.IsRunning.ShouldBeFalse();
        t.TryStart("space-b").ShouldNotBeNull("the slot must be reusable after Finish");
    }

    [Fact]
    public void Current_Is_Null_When_Idle()
    {
        var t = new ReindexJobTracker();
        t.Current.ShouldBeNull();
    }

    [Fact]
    public void Current_Reflects_The_Running_Snapshot_And_Shares_The_Stats_Reference()
    {
        var t = new ReindexJobTracker();
        var stats = t.TryStart("space-a");

        var cur = t.Current;
        cur.ShouldNotBeNull();
        cur!.Space.ShouldBe("space-a");
        // The handler increments this object; status reads must see the same one.
        ReferenceEquals(cur.Stats, stats).ShouldBeTrue();

        stats!.Scanned = 7;
        t.Current!.Stats.Scanned.ShouldBe(7);
    }

    [Fact]
    public void TryStart_Null_Space_Is_Reported_As_All()
    {
        var t = new ReindexJobTracker();
        t.TryStart(null).ShouldNotBeNull();
        t.Current!.Space.ShouldBe("__all__");
    }

    [Fact]
    public void LastRun_Is_Null_Before_Any_Run()
    {
        new ReindexJobTracker().LastRun.ShouldBeNull();
    }

    [Fact]
    public void Finish_Captures_LastRun_With_Outcome_And_Final_Counts()
    {
        var t = new ReindexJobTracker();
        var stats = t.TryStart("space-a");
        stats!.Spaces = 2;
        stats.Scanned = 50;
        stats.Embedded = 40;
        stats.Skipped = 8;
        stats.Failed = 2;
        stats.Error = null;

        t.Finish("completed");

        // Snapshot is gone (idle) but the summary survives for status reads.
        t.Current.ShouldBeNull();
        var last = t.LastRun;
        last.ShouldNotBeNull();
        last!.Outcome.ShouldBe("completed");
        last.Space.ShouldBe("space-a");
        last.Spaces.ShouldBe(2);
        last.Scanned.ShouldBe(50);
        last.Embedded.ShouldBe(40);
        last.Skipped.ShouldBe(8);
        last.Failed.ShouldBe(2);
        last.FinishedAt.ShouldBeGreaterThanOrEqualTo(last.StartedAt);
    }

    [Fact]
    public void Finish_Without_A_Started_Run_Does_Not_Throw_Or_Fabricate_A_Summary()
    {
        var t = new ReindexJobTracker();
        Should.NotThrow(() => t.Finish("completed"));
        t.LastRun.ShouldBeNull();
        t.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task TryStart_Under_Contention_Yields_Exactly_One_Winner()
    {
        var t = new ReindexJobTracker();
        const int racers = 200;

        var results = await Task.WhenAll(
            Enumerable.Range(0, racers).Select(i =>
                Task.Run(() => t.TryStart($"space-{i}") is not null)));

        results.Count(won => won).ShouldBe(1, "exactly one racer may acquire the slot");
        t.IsRunning.ShouldBeTrue();
    }
}
